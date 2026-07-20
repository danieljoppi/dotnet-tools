using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotnetTools.SnapshotCache;

/// <summary>Factory helpers for <see cref="BucketChange{TKey, TEntity}"/> so batch code reads as
/// <c>BucketChange.Append(key, entities)</c> without spelling out type arguments.</summary>
public static class BucketChange
{
    /// <summary>Appends <paramref name="entities"/> to <paramref name="key"/>'s bucket (creating it if absent).</summary>
    public static BucketChange<TKey, TEntity> Append<TKey, TEntity>(TKey key, params TEntity[] entities)
        where TKey : notnull =>
        new(BucketChangeKind.Append, key, entities, null);

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
    internal readonly TKey Key;
    internal readonly TEntity[]? Entities;
    internal readonly (int Index, TEntity Value)[]? Replacements;

    internal BucketChange(BucketChangeKind kind, TKey key, TEntity[]? entities, (int, TEntity)[]? replacements)
    {
        Kind = kind;
        Key = key;
        Entities = entities;
        Replacements = replacements;
    }
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
    /// <summary>Bucket size at which the array representation is promoted to chunks. At 1,024
    /// elements the last whole-array copy is ≤8 KB for reference entities — far below the LOH
    /// bar — while larger buckets get O(chunk) appends. Buckets never demote.</summary>
    public const int ArrayBucketMaxLength = 1024;

    private const int TargetKeysPerShard = 256;
    private const int MinShardCount = 8;
    private const int MaxShardCount = 1 << 16;

    private static readonly TEntity[] EmptyBucket = [];

    private readonly IEqualityComparer<TKey> _comparer;
    private readonly bool _defaultComparer; // value-type TKey + default comparer → devirtualized hash
    private readonly int _shardShift;
    private readonly object _writeLock = new();
    private TableSnapshot _current;

    /// <param name="keyCapacityHint">Expected number of distinct shared keys; sizes the shard fan-out.</param>
    /// <param name="comparer">Key comparer; defaults to <see cref="EqualityComparer{TKey}.Default"/>.</param>
    public MultiValueSnapshotTable(int keyCapacityHint = 0, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyCapacityHint);
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
    /// shard and bucket is copied at most once per batch; readers see the whole batch or none.</summary>
    public void ApplyChanges(IEnumerable<BucketChange<TKey, TEntity>> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_writeLock)
        {
            var snapshot = _current;
            var shards = (Dictionary<TKey, object>[])snapshot.Shards.Clone();
            var owned = new bool[shards.Length];
            int keyCount = snapshot.KeyCount;

            foreach (var change in changes)
            {
                int shardIndex = ShardOf(change.Key);
                if (!owned[shardIndex])
                {
                    shards[shardIndex] = new Dictionary<TKey, object>(shards[shardIndex], _comparer);
                    owned[shardIndex] = true;
                }
                var shard = shards[shardIndex];
                switch (change.Kind)
                {
                    case BucketChangeKind.Append:
                    {
                        shard.TryGetValue(change.Key, out var existing);
                        if (existing is null)
                        {
                            keyCount++;
                        }
                        shard[change.Key] = AppendToBucket(existing, change.Entities!);
                        break;
                    }
                    case BucketChangeKind.ReplaceAt:
                    {
                        if (!shard.TryGetValue(change.Key, out var existing))
                        {
                            throw new KeyNotFoundException($"Cannot replace entities of unknown key '{change.Key}'.");
                        }
                        shard[change.Key] = ReplaceInBucket(existing, change.Replacements!);
                        break;
                    }
                    case BucketChangeKind.ReplaceBucket:
                    {
                        if (!shard.ContainsKey(change.Key))
                        {
                            keyCount++;
                        }
                        shard[change.Key] = MaterializeBucket(change.Entities!);
                        break;
                    }
                    case BucketChangeKind.Remove:
                    {
                        if (shard.Remove(change.Key))
                        {
                            keyCount--;
                        }
                        break;
                    }
                }
            }

            Volatile.Write(ref _current, new TableSnapshot(this, shards, keyCount));
        }
    }

    /// <summary>Atomically replaces the entire table content.</summary>
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

    private static object AppendToBucket(object? existing, TEntity[] entities)
    {
        switch (existing)
        {
            case ChunkedImmutableList<TEntity> chunked:
            {
                var builder = chunked.ToBuilder();
                builder.AddRange(entities.AsSpan());
                return builder.ToImmutable();
            }
            case TEntity[] bucket when bucket.Length + entities.Length > ArrayBucketMaxLength:
            {
                // Promote: one final whole copy into sub-LOH chunks; O(chunk) appends from here on.
                var builder = ChunkedImmutableList<TEntity>.Empty.ToBuilder();
                builder.AddRange(bucket.AsSpan());
                builder.AddRange(entities.AsSpan());
                return builder.ToImmutable();
            }
            default:
            {
                var bucket = existing as TEntity[] ?? EmptyBucket;
                var grown = new TEntity[bucket.Length + entities.Length];
                Array.Copy(bucket, grown, bucket.Length);
                Array.Copy(entities, 0, grown, bucket.Length, entities.Length);
                return grown;
            }
        }
    }

    private static object ReplaceInBucket(object existing, (int Index, TEntity Value)[] replacements)
    {
        if (existing is ChunkedImmutableList<TEntity> chunked)
        {
            var builder = chunked.ToBuilder();
            foreach (var (index, value) in replacements)
            {
                builder[index] = value;
            }
            return builder.ToImmutable();
        }
        var bucket = (TEntity[])existing;
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

    private static object MaterializeBucket(IReadOnlyList<TEntity> entities)
    {
        if (entities.Count <= ArrayBucketMaxLength)
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
