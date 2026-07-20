namespace DotnetTools.SnapshotCache;

/// <summary>
/// Handle for a secondary index registered with <see cref="SnapshotTable{TKey,TValue}.CreateIndex"/>.
/// Query through a snapshot: <c>table.GetSnapshot().Lookup(index, indexKey)</c>.
/// </summary>
public sealed class TableIndex<TKey, TValue, TIndexKey>
    where TKey : notnull
    where TIndexKey : notnull
{
    internal readonly int Ordinal;
    internal readonly Func<TKey, TValue, TIndexKey> Selector;
    internal readonly IEqualityComparer<TIndexKey> Comparer;

    internal TableIndex(int ordinal, Func<TKey, TValue, TIndexKey> selector, IEqualityComparer<TIndexKey> comparer)
    {
        Ordinal = ordinal;
        Selector = selector;
        Comparer = comparer;
    }
}

/// <summary>Base for the immutable snapshot state of one secondary index, erased over the
/// index-key type so <see cref="SnapshotTable{TKey,TValue}"/> can hold a heterogeneous set.</summary>
internal abstract class IndexState<TKey, TValue>
    where TKey : notnull
{
    internal abstract object CreateWriter();

    internal abstract IndexState<TKey, TValue> Freeze(object writerState);

    /// <summary>Applies one row transition: leave the old value's bucket (if <paramref name="hadOld"/>)
    /// and/or enter the new value's bucket (if <paramref name="hasNew"/>).</summary>
    internal abstract void Apply(
        object writerState, TKey key, bool hadOld, TValue? oldValue, bool hasNew, TValue? newValue);
}

/// <summary>
/// Snapshot state of one secondary index: index key → list of primary keys, stored as a fixed
/// number of copy-on-write dictionary shards. Buckets are hybrid (ADR-0006): plain arrays while
/// small — the best read/scan representation, copied whole on change — and
/// <see cref="ChunkedImmutableList{T}"/> once they cross <see cref="ArrayBucketMaxLength"/>
/// elements, so hot one-to-many groups (10k–100k+ members) update at O(chunk) cost and no index
/// bucket ever becomes a Large Object Heap array.
/// </summary>
internal sealed class IndexState<TKey, TValue, TIndexKey> : IndexState<TKey, TValue>
    where TKey : notnull
    where TIndexKey : notnull
{
    // Above this element count an array bucket is promoted to a chunked list: each further
    // membership change stops copying the whole bucket (8 KB at 1024 8-byte keys, and O(n)
    // per change) and starts copying one chunk + spine. Well below the ~10,625-reference LOH bar.
    internal const int ArrayBucketMaxLength = 1024;

    private readonly TableIndex<TKey, TValue, TIndexKey> _definition;
    private readonly bool _defaultComparer; // value-type index key + default comparer → devirtualized path
    // Bucket is TKey[] (small) or ChunkedImmutableList<TKey> (promoted) — both IReadOnlyList<TKey>.
    private readonly Dictionary<TIndexKey, object>[] _shards;
    private readonly int _shardMask;

    internal IndexState(TableIndex<TKey, TValue, TIndexKey> definition, int shardCount)
    {
        _definition = definition;
        _defaultComparer = typeof(TIndexKey).IsValueType
            && ReferenceEquals(definition.Comparer, EqualityComparer<TIndexKey>.Default);
        _shards = new Dictionary<TIndexKey, object>[shardCount];
        Array.Fill(_shards, EmptyShard);
        _shardMask = shardCount - 1;
    }

    private IndexState(TableIndex<TKey, TValue, TIndexKey> definition, Dictionary<TIndexKey, object>[] shards)
    {
        _definition = definition;
        _defaultComparer = typeof(TIndexKey).IsValueType
            && ReferenceEquals(definition.Comparer, EqualityComparer<TIndexKey>.Default);
        _shards = shards;
        _shardMask = shards.Length - 1;
    }

    private static readonly Dictionary<TIndexKey, object> EmptyShard = [];
    private static readonly TKey[] EmptyBucket = [];

    // Same devirtualization trick as ShardMap: EqualityComparer<T>.Default is a JIT intrinsic
    // that inlines hash/equality for value-type index keys; reference types keep the plain path.
    private int HashOf(TIndexKey key) =>
        typeof(TIndexKey).IsValueType && _defaultComparer
            ? EqualityComparer<TIndexKey>.Default.GetHashCode(key)
            : _definition.Comparer.GetHashCode(key);

    private bool IndexKeyEquals(TIndexKey a, TIndexKey b) =>
        typeof(TIndexKey).IsValueType && _defaultComparer
            ? EqualityComparer<TIndexKey>.Default.Equals(a, b)
            : _definition.Comparer.Equals(a, b);

    private int ShardOf(TIndexKey key) =>
        (int)((uint)(HashOf(key) * -1640531527) >> 16) & _shardMask;

    internal IReadOnlyList<TKey> Lookup(TIndexKey indexKey) =>
        _shards[ShardOf(indexKey)].TryGetValue(indexKey, out var bucket)
            ? (IReadOnlyList<TKey>)bucket
            : EmptyBucket;

    private sealed class Writer
    {
        internal required Dictionary<TIndexKey, object>[] Shards;
        internal required bool[] Owned;

        // Promoted buckets touched by this batch, mutated through one builder each so N membership
        // changes to the same hot group cost one spine copy, not N publishes. Folded back into the
        // shards on Freeze. (The Zipf hot head sees 1–50 appends per touched key per batch.)
        internal Dictionary<TIndexKey, ChunkedImmutableList<TKey>.Builder>? Building;
    }

    internal override object CreateWriter() =>
        new Writer { Shards = (Dictionary<TIndexKey, object>[])_shards.Clone(), Owned = new bool[_shards.Length] };

    internal override IndexState<TKey, TValue> Freeze(object writerState)
    {
        var writer = (Writer)writerState;
        if (writer.Building is { } building)
        {
            foreach (var (indexKey, builder) in building)
            {
                var shard = GetWritableShard(writer, indexKey);
                if (builder.Count == 0)
                {
                    shard.Remove(indexKey);
                }
                else
                {
                    shard[indexKey] = builder.ToImmutable();
                }
            }
        }
        return new IndexState<TKey, TValue, TIndexKey>(_definition, writer.Shards);
    }

    internal override void Apply(
        object writerState, TKey key, bool hadOld, TValue? oldValue, bool hasNew, TValue? newValue)
    {
        TIndexKey? oldIndexKey = hadOld ? _definition.Selector(key, oldValue!) : default;
        TIndexKey? newIndexKey = hasNew ? _definition.Selector(key, newValue!) : default;
        if (hadOld && hasNew && IndexKeyEquals(oldIndexKey!, newIndexKey!))
        {
            return; // value changed but its index key didn't — no bucket movement
        }
        var writer = (Writer)writerState;
        if (hadOld)
        {
            RemoveFromBucket(writer, oldIndexKey!, key);
        }
        if (hasNew)
        {
            AddToBucket(writer, newIndexKey!, key);
        }
    }

    private Dictionary<TIndexKey, object> GetWritableShard(Writer writer, TIndexKey indexKey)
    {
        int shardIndex = ShardOf(indexKey);
        if (!writer.Owned[shardIndex])
        {
            writer.Shards[shardIndex] = new Dictionary<TIndexKey, object>(writer.Shards[shardIndex], _definition.Comparer);
            writer.Owned[shardIndex] = true;
        }
        return writer.Shards[shardIndex];
    }

    /// <summary>Registers <paramref name="builder"/> as this batch's pending state for the bucket;
    /// the stale shard entry is replaced when the batch freezes.</summary>
    private ChunkedImmutableList<TKey>.Builder OpenBuilder(
        Writer writer, TIndexKey indexKey, ChunkedImmutableList<TKey>.Builder builder)
    {
        (writer.Building ??= new Dictionary<TIndexKey, ChunkedImmutableList<TKey>.Builder>(_definition.Comparer))[indexKey] = builder;
        return builder;
    }

    private void AddToBucket(Writer writer, TIndexKey indexKey, TKey key)
    {
        if (writer.Building is { } building && building.TryGetValue(indexKey, out var pending))
        {
            pending.Add(key); // bucket already open this batch: O(chunk), no publish until Freeze
            return;
        }
        var shard = GetWritableShard(writer, indexKey);
        shard.TryGetValue(indexKey, out var existing);
        switch (existing)
        {
            case ChunkedImmutableList<TKey> chunked:
            {
                // Promoted bucket: append copies one chunk + spine, never the whole bucket.
                OpenBuilder(writer, indexKey, chunked.ToBuilder()).Add(key);
                break;
            }
            case TKey[] bucket when bucket.Length >= ArrayBucketMaxLength:
            {
                // Promote: one final full copy into chunks; O(chunk) from here on.
                var builder = OpenBuilder(writer, indexKey, ChunkedImmutableList<TKey>.Empty.ToBuilder());
                builder.AddRange(bucket.AsSpan());
                builder.Add(key);
                break;
            }
            default:
            {
                var bucket = existing as TKey[] ?? EmptyBucket;
                var grown = new TKey[bucket.Length + 1];
                Array.Copy(bucket, grown, bucket.Length);
                grown[bucket.Length] = key;
                shard[indexKey] = grown;
                break;
            }
        }
    }

    private void RemoveFromBucket(Writer writer, TIndexKey indexKey, TKey key)
    {
        if (writer.Building is { } building && building.TryGetValue(indexKey, out var pending))
        {
            RemoveFromBuilder(pending, key);
            return;
        }
        var shard = GetWritableShard(writer, indexKey);
        if (!shard.TryGetValue(indexKey, out var existing))
        {
            return;
        }
        if (existing is ChunkedImmutableList<TKey> chunked)
        {
            // Swap-remove: order within a bucket is unspecified (same contract as the row store).
            // The builder is kept open for the rest of the batch; Freeze removes the key if it
            // empties out.
            RemoveFromBuilder(OpenBuilder(writer, indexKey, chunked.ToBuilder()), key);
            return;
        }
        var bucket = (TKey[])existing;
        int arrayPosition = Array.IndexOf(bucket, key);
        if (arrayPosition < 0)
        {
            return;
        }
        if (bucket.Length == 1)
        {
            shard.Remove(indexKey);
            return;
        }
        var shrunk = new TKey[bucket.Length - 1];
        Array.Copy(bucket, shrunk, arrayPosition);
        Array.Copy(bucket, arrayPosition + 1, shrunk, arrayPosition, bucket.Length - arrayPosition - 1);
        shard[indexKey] = shrunk;
    }

    /// <summary>Swap-remove within a batch's open bucket builder: O(scan) to find the key,
    /// O(chunk) to relocate the last element.</summary>
    private static void RemoveFromBuilder(ChunkedImmutableList<TKey>.Builder builder, TKey key)
    {
        int count = builder.Count;
        for (int i = 0; i < count; i++)
        {
            if (EqualityComparer<TKey>.Default.Equals(builder[i], key))
            {
                int last = count - 1;
                if (i != last)
                {
                    builder[i] = builder[last];
                }
                builder.RemoveLast();
                return;
            }
        }
    }
}
