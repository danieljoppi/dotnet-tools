using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotnetTools.SnapshotCache;

/// <summary>Factory helpers for <see cref="BucketChange{TKey, TEntity}"/> so batch code reads as
/// <c>BucketChange.Append(key, entities)</c> without spelling out type arguments.</summary>
public static class BucketChange
{
    /// <summary>Appends <paramref name="entities"/> to <paramref name="key"/>'s bucket (creating it if absent).</summary>
    public static BucketChange<TKey, TEntity> Append<TKey, TEntity>(TKey key, params TEntity[] entities)
        where TKey : notnull =>
        new(BucketChangeKind.Append, key, entities, null);

    /// <summary>Appends a <b>single</b> entity to <paramref name="key"/>'s bucket without allocating
    /// a backing array — the lean path for the common one-entity refresh (issue #45). Prefer this
    /// over <c>Append(key, new[] { entity })</c>: at scale, per-change one-element arrays were the
    /// largest LOH adder on the incremental-refresh path.</summary>
    public static BucketChange<TKey, TEntity> Append<TKey, TEntity>(TKey key, TEntity entity)
        where TKey : notnull =>
        new(BucketChangeKind.Append, key, entity);

    /// <summary>Replaces the entities at the given bucket positions of an existing key.</summary>
    public static BucketChange<TKey, TEntity> ReplaceAt<TKey, TEntity>(TKey key, params (int Index, TEntity Value)[] replacements)
        where TKey : notnull =>
        new(BucketChangeKind.ReplaceAt, key, null, replacements);

    /// <summary>Replaces <paramref name="key"/>'s entire bucket (creating it if absent).</summary>
    public static BucketChange<TKey, TEntity> ReplaceBucket<TKey, TEntity>(TKey key, params TEntity[] entities)
        where TKey : notnull =>
        new(BucketChangeKind.ReplaceBucket, key, entities, null);

    /// <summary>Removes <paramref name="key"/> and its bucket; ignored if the key is absent.</summary>
    public static BucketChange<TKey, TEntity> Remove<TKey, TEntity>(TKey key)
        where TKey : notnull =>
        new(BucketChangeKind.Remove, key, null, null);
}

internal enum BucketChangeKind
{
    Append,
    ReplaceAt,
    ReplaceBucket,
    Remove,
}

/// <summary>One change to one shared key's bucket inside a <see cref="MultiValueSnapshotTable{TKey, TEntity}"/> batch.</summary>
public readonly struct BucketChange<TKey, TEntity>
    where TKey : notnull
{
    internal readonly BucketChangeKind Kind;
    // A single-entity Append (issue #45) stores its one entity inline in SingleEntity with no
    // backing array; HasSingleEntity selects that path. The bool packs into Kind's alignment slot,
    // so the only size cost is the SingleEntity field. Set only for Append.
    internal readonly bool HasSingleEntity;
    internal readonly TKey Key;
    internal readonly TEntity[]? Entities;
    internal readonly (int Index, TEntity Value)[]? Replacements;
    internal readonly TEntity? SingleEntity;

    internal BucketChange(BucketChangeKind kind, TKey key, TEntity[]? entities, (int, TEntity)[]? replacements)
    {
        Kind = kind;
        Key = key;
        Entities = entities;
        Replacements = replacements;
    }

    // Single-entity Append: the entity is held inline, no array allocated.
    internal BucketChange(BucketChangeKind kind, TKey key, TEntity entity)
    {
        Kind = kind;
        Key = key;
        HasSingleEntity = true;
        SingleEntity = entity;
    }

    /// <summary>The entities this Append adds, as a span over either the inline single entity or the
    /// backing array — no allocation either way. Valid only while this change is on the stack.</summary>
    internal ReadOnlySpan<TEntity> AppendEntities =>
        HasSingleEntity
            // SingleEntity is TEntity? (the array ctor leaves it default); reinterpret the ref as
            // non-nullable TEntity — identical runtime type — for a one-element span.
            ? MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<TEntity?, TEntity>(ref Unsafe.AsRef(in SingleEntity)), 1)
            : Entities;
}

/// <summary>
/// The shared-key → many-values cache table (one key → a bucket of entities), packaging the
/// pattern the issue-#6 benchmarks proved out (ADR-0006): wait-free snapshot reads, atomic
/// <see cref="ApplyChanges"/> batches costing O(touched buckets), and hybrid bucket storage —
/// a flat array while a bucket is small (the best read/scan representation), promoted to a
/// <see cref="ChunkedImmutableList{T}"/> once it crosses <see cref="ArrayBucketMaxLength"/>
/// elements so warm appends copy one sub-LOH chunk instead of the whole bucket.
/// <b>No bucket, shard, or bookkeeping array ever reaches the Large Object Heap, at any bucket
/// size.</b> Writers serialize; readers never lock and always observe a whole batch or none of it.
/// </summary>
public sealed class MultiValueSnapshotTable<TKey, TEntity>
    where TKey : notnull
{
    /// <summary>Upper bound on the element count of a flat-array bucket before it is promoted to
    /// chunks. At 1,024 elements the last whole-array copy is ≤8 KB for reference entities — far
    /// below the LOH bar. For large <b>value-type</b> entities the <i>effective</i> cap is lower
    /// (see <see cref="ArrayBucketMaxCount"/>) so the flat array itself never reaches the LOH.
    /// Buckets never demote.</summary>
    public const int ArrayBucketMaxLength = 1024;

    /// <summary>Bytes a flat-array bucket may occupy before promotion — kept below the 85,000-byte
    /// LOH threshold with margin. The effective element cap is derived from this and the element
    /// size (issue #44).</summary>
    private const int ArrayBucketMaxBytes = 84_000;

    /// <summary>The <b>default</b> effective promotion cap in elements: <see cref="ArrayBucketMaxLength"/>,
    /// but lowered for wide entities so a flat <c>TEntity[]</c> never reaches the LOH. For reference
    /// entities (8-byte slots) this is 1,024; for a struct larger than ~82 bytes it drops below
    /// 1,024 (e.g. a 1 KB struct promotes at ~82 elements, an ~82 KB array). Byte-awareness only
    /// <i>tightens</i> the cap here; a table may <i>raise</i> the ceiling via the constructor's
    /// <c>maxArrayBucketElements</c> (still floored by the byte limit) — the retained-heap lever for
    /// issue #44. Computed once per closed generic type.</summary>
    internal static readonly int ArrayBucketMaxCount =
        Math.Min(ArrayBucketMaxLength, Math.Max(1, ArrayBucketMaxBytes / Unsafe.SizeOf<TEntity>()));

    /// <summary>This instance's effective promotion cap (honors <c>maxArrayBucketElements</c>).</summary>
    internal int EffectiveArrayBucketMaxCount => _arrayBucketMaxCount;

    private const int TargetKeysPerShard = 256;
    private const int MinShardCount = 8;
    private const int MaxShardCount = 1 << 16;

    private static readonly TEntity[] EmptyBucket = [];

    private readonly IEqualityComparer<TKey> _comparer;
    private readonly bool _defaultComparer; // value-type TKey + default comparer → devirtualized hash
    private readonly int _shardShift;
    // Per-instance effective promotion cap: the configured element ceiling, floored by the
    // byte-aware limit so a flat TEntity[] can never reach the LOH (issue #44). Defaults to
    // ArrayBucketMaxCount (ceiling = ArrayBucketMaxLength).
    private readonly int _arrayBucketMaxCount;
    private readonly object _writeLock = new();
    private TableSnapshot _current;

    /// <summary>How many promoted-bucket builders the last <see cref="ApplyChanges"/> batch
    /// folded back — i.e. chunked publishes that batch paid. Test instrumentation for the
    /// issue-#31 acceptance ("N same-key appends → one publish"), assertable deterministically
    /// through InternalsVisibleTo; one int write per batch, under the write lock.</summary>
    internal int PromotedPublishesInLastBatch;

    /// <param name="keyCapacityHint">Expected number of distinct shared keys; sizes the shard fan-out.</param>
    /// <param name="comparer">Key comparer; defaults to <see cref="EqualityComparer{TKey}.Default"/>.</param>
    /// <param name="maxArrayBucketElements">
    /// Element ceiling at which a flat-array bucket is promoted to chunks; <c>0</c> uses the default
    /// <see cref="ArrayBucketMaxLength"/> (1,024). Raising this keeps larger buckets as compact flat
    /// arrays — fewer <see cref="ChunkedImmutableList{T}"/> instances, so less per-instance overhead
    /// (the retained-heap lever for issue #44) — at the cost of a larger whole-array copy per append
    /// to a bucket in the widened range. <b>The value is a ceiling only:</b> it is always floored by
    /// the byte-aware limit so a flat <c>TEntity[]</c> can never reach the LOH, whatever you pass.
    /// Size it from your bucket-size distribution; the default is safe but conservative for
    /// reference entities. Does not affect already-promoted buckets.
    /// </param>
    public MultiValueSnapshotTable(
        int keyCapacityHint = 0, IEqualityComparer<TKey>? comparer = null, int maxArrayBucketElements = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyCapacityHint);
        ArgumentOutOfRangeException.ThrowIfNegative(maxArrayBucketElements);
        int ceiling = maxArrayBucketElements == 0 ? ArrayBucketMaxLength : maxArrayBucketElements;
        _arrayBucketMaxCount = Math.Min(ceiling, Math.Max(1, ArrayBucketMaxBytes / Unsafe.SizeOf<TEntity>()));
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
        _defaultComparer = typeof(TKey).IsValueType && ReferenceEquals(_comparer, EqualityComparer<TKey>.Default);
        int shardCount = (int)BitOperations.RoundUpToPowerOf2(
            (uint)Math.Clamp(keyCapacityHint / TargetKeysPerShard, MinShardCount, MaxShardCount));
        _shardShift = 32 - BitOperations.Log2((uint)shardCount);
        var shards = new Dictionary<TKey, object>[shardCount];
        Array.Fill(shards, EmptyShard);
        _current = new TableSnapshot(this, shards, 0);
    }

    private static readonly Dictionary<TKey, object> EmptyShard = [];

    // Same devirtualization trick as ShardMap/SnapshotTable (PR #16): for value-type keys with
    // the default comparer, EqualityComparer<TKey>.Default is a JIT intrinsic that inlines the
    // hash instead of interface-dispatching; the typeof guard folds away at JIT time.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ShardOf(TKey key)
    {
        int hash = typeof(TKey).IsValueType && _defaultComparer
            ? EqualityComparer<TKey>.Default.GetHashCode(key)
            : _comparer.GetHashCode(key);
        return (int)((uint)(hash * -1640531527 /* Fibonacci hashing */) >> _shardShift);
    }

    /// <summary>Number of distinct shared keys in the current snapshot.</summary>
    public int KeyCount => Volatile.Read(ref _current).KeyCount;

    /// <summary>Wait-free bucket lookup against the current snapshot. Returns an empty list for
    /// unknown keys. The result is immutable; hold it as long as needed.</summary>
    public IReadOnlyList<TEntity> Lookup(TKey key) => Volatile.Read(ref _current).Lookup(key);

    public bool ContainsKey(TKey key) => Volatile.Read(ref _current).ContainsKey(key);

    /// <summary>The current immutable snapshot — use when several lookups must observe one
    /// consistent version of every bucket.</summary>
    public TableSnapshot GetSnapshot() => Volatile.Read(ref _current);

    /// <summary>Applies a batch of bucket changes as one atomic snapshot transition. Each touched
    /// shard and bucket is copied at most once per batch; readers see the whole batch or none.
    /// Successive <c>Append</c>/<c>ReplaceAt</c> changes to the same promoted (chunked) key fold
    /// into one builder published once at the end of the batch — mirroring the secondary index's
    /// per-batch builder cache (issue #31) — so N changes to a hot key cost one spine copy, not N.</summary>
    /// <remarks>
    /// <para><b>Cost is O(the total occupancy of the shards this batch touches), paid once per
    /// batch — not O(1) per key.</b> Each call clones the shard directory and copy-on-writes every
    /// shard dictionary it touches, then publishes a single snapshot. Amortized over a well-sized
    /// batch that is cheap; paid once per key it is not.</para>
    /// <para><b>Never cold-load by calling this once per key.</b> A loop of single-change
    /// <see cref="ApplyChanges"/> calls re-clones a touched shard dictionary on every call, so
    /// loading N keys is O(N²) in shard occupancy and floods the Large Object Heap with dead shard
    /// arrays — the footgun that kept a production process unhealthy for 15+ minutes (issue #42).
    /// To load a whole table, group entities by key and call <see cref="Reset"/>, or pass the
    /// entire cold load to this method as one batch. Per-entity calls are fine only for small
    /// incremental refreshes.</para>
    /// <para><b>Keep the batch input lean.</b> This method only <c>foreach</c>es
    /// <paramref name="changes"/>, so a lazy <c>keys.Select(k =&gt; BucketChange.Append(k, entity))</c>
    /// never allocates a <c>BucketChange[]</c> — at large batch sizes that array was itself a top LOH
    /// contributor (issue #45). For single-entity appends prefer <c>BucketChange.Append(key, entity)</c>,
    /// which holds the entity inline instead of allocating a one-element array per change.</para>
    /// </remarks>
    public void ApplyChanges(IEnumerable<BucketChange<TKey, TEntity>> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_writeLock)
        {
            var snapshot = _current;
            var shards = (Dictionary<TKey, object>[])snapshot.Shards.Clone();
            var owned = new bool[shards.Length];
            int keyCount = snapshot.KeyCount;
            // Promoted buckets touched by this batch, mutated through one builder each. While a
            // builder is open its key's shard entry (if any) is stale; every path below consults
            // the builder map first, and the fold-back loop at the end replaces the stale entries.
            Dictionary<TKey, ChunkedImmutableList<TEntity>.Builder>? building = null;

            foreach (var change in changes)
            {
                var shard = GetWritableShard(shards, owned, change.Key);
                switch (change.Kind)
                {
                    case BucketChangeKind.Append:
                    {
                        // Entities to append: a span over the inline single entity (issue #45) or
                        // the backing array — no allocation either way.
                        var entities = change.AppendEntities;
                        if (building is not null && building.TryGetValue(change.Key, out var pending))
                        {
                            pending.AddRange(entities); // O(chunk), no publish until batch end
                            break;
                        }
                        shard.TryGetValue(change.Key, out var existing);
                        if (existing is null)
                        {
                            keyCount++;
                        }
                        if (existing is ChunkedImmutableList<TEntity> chunked)
                        {
                            OpenBuilder(ref building, change.Key, chunked.ToBuilder())
                                .AddRange(entities);
                        }
                        else
                        {
                            var bucket = existing as TEntity[] ?? EmptyBucket;
                            if (bucket.Length + entities.Length > _arrayBucketMaxCount)
                            {
                                // Promote: one final whole copy into sub-LOH chunks; O(chunk)
                                // appends from here on.
                                var builder = OpenBuilder(ref building, change.Key,
                                    ChunkedImmutableList<TEntity>.Empty.ToBuilder());
                                builder.AddRange(bucket.AsSpan());
                                builder.AddRange(entities);
                            }
                            else
                            {
                                var grown = new TEntity[bucket.Length + entities.Length];
                                Array.Copy(bucket, grown, bucket.Length);
                                entities.CopyTo(grown.AsSpan(bucket.Length));
                                shard[change.Key] = grown;
                            }
                        }
                        break;
                    }
                    case BucketChangeKind.ReplaceAt:
                    {
                        if (building is not null && building.TryGetValue(change.Key, out var pending))
                        {
                            foreach (var (index, value) in change.Replacements!)
                            {
                                pending[index] = value;
                            }
                            break;
                        }
                        if (!shard.TryGetValue(change.Key, out var existing))
                        {
                            throw new KeyNotFoundException($"Cannot replace entities of unknown key '{change.Key}'.");
                        }
                        if (existing is ChunkedImmutableList<TEntity> chunked)
                        {
                            var builder = OpenBuilder(ref building, change.Key, chunked.ToBuilder());
                            foreach (var (index, value) in change.Replacements!)
                            {
                                builder[index] = value;
                            }
                        }
                        else
                        {
                            shard[change.Key] = ReplaceInArrayBucket((TEntity[])existing, change.Replacements!);
                        }
                        break;
                    }
                    case BucketChangeKind.ReplaceBucket:
                    {
                        if (!shard.ContainsKey(change.Key) && building?.ContainsKey(change.Key) != true)
                        {
                            keyCount++;
                        }
                        building?.Remove(change.Key); // pending appends discarded: last change wins
                        shard[change.Key] = MaterializeBucket(change.Entities!);
                        break;
                    }
                    case BucketChangeKind.Remove:
                    {
                        bool had = shard.Remove(change.Key);
                        if (building?.Remove(change.Key) == true)
                        {
                            had = true;
                        }
                        if (had)
                        {
                            keyCount--;
                        }
                        break;
                    }
                }
            }

            PromotedPublishesInLastBatch = building?.Count ?? 0;
            if (building is not null)
            {
                foreach (var (key, builder) in building)
                {
                    GetWritableShard(shards, owned, key)[key] = builder.ToImmutable();
                }
            }

            Volatile.Write(ref _current, new TableSnapshot(this, shards, keyCount));
        }
    }

    private Dictionary<TKey, object> GetWritableShard(
        Dictionary<TKey, object>[] shards, bool[] owned, TKey key)
    {
        int shardIndex = ShardOf(key);
        if (!owned[shardIndex])
        {
            shards[shardIndex] = new Dictionary<TKey, object>(shards[shardIndex], _comparer);
            owned[shardIndex] = true;
        }
        return shards[shardIndex];
    }

    /// <summary>Registers <paramref name="builder"/> as the batch's pending state for the key;
    /// the stale shard entry (if any) is replaced when the batch folds builders back.</summary>
    private ChunkedImmutableList<TEntity>.Builder OpenBuilder(
        ref Dictionary<TKey, ChunkedImmutableList<TEntity>.Builder>? building,
        TKey key, ChunkedImmutableList<TEntity>.Builder builder)
    {
        (building ??= new Dictionary<TKey, ChunkedImmutableList<TEntity>.Builder>(_comparer))[key] = builder;
        return builder;
    }

    /// <summary>Atomically replaces the entire table content.</summary>
    /// <remarks>The one-shot cold-load path: builds every shard once in O(total entities) with no
    /// per-key copy-on-write, and never touches the Large Object Heap. Prefer this — or a single
    /// batched <see cref="ApplyChanges"/> — over a per-key <see cref="ApplyChanges"/> loop when
    /// loading a whole table; see the remarks on <see cref="ApplyChanges"/> for why the loop is
    /// O(N²).</remarks>
    public void Reset(IEnumerable<KeyValuePair<TKey, IReadOnlyList<TEntity>>> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        lock (_writeLock)
        {
            var shards = new Dictionary<TKey, object>[_current.Shards.Length];
            for (int i = 0; i < shards.Length; i++)
            {
                shards[i] = [];
            }
            int keyCount = 0;
            foreach (var (key, entities) in buckets)
            {
                var shard = shards[ShardOf(key)];
                if (!shard.ContainsKey(key))
                {
                    keyCount++;
                }
                shard[key] = MaterializeBucket(entities);
            }
            Volatile.Write(ref _current, new TableSnapshot(this, shards, keyCount));
        }
    }

    /// <summary>Removes all keys and buckets.</summary>
    public void Clear() => Reset([]);

    private static TEntity[] ReplaceInArrayBucket(TEntity[] bucket, (int Index, TEntity Value)[] replacements)
    {
        var copy = new TEntity[bucket.Length];
        Array.Copy(bucket, copy, bucket.Length);
        foreach (var (index, value) in replacements)
        {
            if ((uint)index >= (uint)copy.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(replacements), index, "Replacement index is outside the bucket.");
            }
            copy[index] = value;
        }
        return copy;
    }

    private object MaterializeBucket(IReadOnlyList<TEntity> entities)
    {
        if (entities.Count <= _arrayBucketMaxCount)
        {
            if (entities is TEntity[] source)
            {
                var copy = new TEntity[source.Length];
                Array.Copy(source, copy, source.Length);
                return copy;
            }
            var array = new TEntity[entities.Count];
            for (int i = 0; i < array.Length; i++)
            {
                array[i] = entities[i];
            }
            return array;
        }
        var builder = ChunkedImmutableList<TEntity>.Empty.ToBuilder();
        builder.AddRange(entities);
        return builder.ToImmutable();
    }

    /// <summary>A fully immutable, internally consistent point-in-time view of every bucket.</summary>
    public sealed class TableSnapshot
    {
        private readonly MultiValueSnapshotTable<TKey, TEntity> _table;
        internal readonly Dictionary<TKey, object>[] Shards;

        /// <summary>Number of distinct shared keys in this snapshot.</summary>
        public int KeyCount { get; }

        internal TableSnapshot(MultiValueSnapshotTable<TKey, TEntity> table, Dictionary<TKey, object>[] shards, int keyCount)
        {
            _table = table;
            Shards = shards;
            KeyCount = keyCount;
        }

        /// <summary>The key's bucket in this snapshot, or an empty list for unknown keys.</summary>
        public IReadOnlyList<TEntity> Lookup(TKey key) =>
            Shards[_table.ShardOf(key)].TryGetValue(key, out var bucket)
                ? (IReadOnlyList<TEntity>)bucket
                : EmptyBucket;

        public bool ContainsKey(TKey key) => Shards[_table.ShardOf(key)].ContainsKey(key);

        /// <summary>All shared keys in this snapshot (unordered).</summary>
        public IEnumerable<TKey> Keys
        {
            get
            {
                foreach (var shard in Shards)
                {
                    foreach (var key in shard.Keys)
                    {
                        yield return key;
                    }
                }
            }
        }
    }
}
