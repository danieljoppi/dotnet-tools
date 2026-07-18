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
/// Snapshot state of one secondary index: index key → array of primary keys, stored as a fixed
/// number of copy-on-write dictionary shards. Buckets are plain arrays, copied on change — suited
/// to bucket sizes up to the low thousands of keys (a bucket stays below the LOH threshold up to
/// ~10k 8-byte keys). Designed for moderate-cardinality attributes (region, status, tier, ...).
/// </summary>
internal sealed class IndexState<TKey, TValue, TIndexKey> : IndexState<TKey, TValue>
    where TKey : notnull
    where TIndexKey : notnull
{
    private readonly TableIndex<TKey, TValue, TIndexKey> _definition;
    private readonly Dictionary<TIndexKey, TKey[]>[] _shards;
    private readonly int _shardMask;

    internal IndexState(TableIndex<TKey, TValue, TIndexKey> definition, int shardCount)
    {
        _definition = definition;
        _shards = new Dictionary<TIndexKey, TKey[]>[shardCount];
        Array.Fill(_shards, EmptyShard);
        _shardMask = shardCount - 1;
    }

    private IndexState(TableIndex<TKey, TValue, TIndexKey> definition, Dictionary<TIndexKey, TKey[]>[] shards)
    {
        _definition = definition;
        _shards = shards;
        _shardMask = shards.Length - 1;
    }

    private static readonly Dictionary<TIndexKey, TKey[]> EmptyShard = [];
    private static readonly TKey[] EmptyBucket = [];

    private int ShardOf(TIndexKey key) =>
        (int)((uint)(_definition.Comparer.GetHashCode(key) * -1640531527) >> 16) & _shardMask;

    internal TKey[] Lookup(TIndexKey indexKey) =>
        _shards[ShardOf(indexKey)].TryGetValue(indexKey, out var bucket) ? bucket : EmptyBucket;

    private sealed class Writer
    {
        internal required Dictionary<TIndexKey, TKey[]>[] Shards;
        internal required bool[] Owned;
    }

    internal override object CreateWriter() =>
        new Writer { Shards = (Dictionary<TIndexKey, TKey[]>[])_shards.Clone(), Owned = new bool[_shards.Length] };

    internal override IndexState<TKey, TValue> Freeze(object writerState) =>
        new IndexState<TKey, TValue, TIndexKey>(_definition, ((Writer)writerState).Shards);

    internal override void Apply(
        object writerState, TKey key, bool hadOld, TValue? oldValue, bool hasNew, TValue? newValue)
    {
        TIndexKey? oldIndexKey = hadOld ? _definition.Selector(key, oldValue!) : default;
        TIndexKey? newIndexKey = hasNew ? _definition.Selector(key, newValue!) : default;
        if (hadOld && hasNew && _definition.Comparer.Equals(oldIndexKey!, newIndexKey!))
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

    private Dictionary<TIndexKey, TKey[]> GetWritableShard(Writer writer, TIndexKey indexKey)
    {
        int shardIndex = ShardOf(indexKey);
        if (!writer.Owned[shardIndex])
        {
            writer.Shards[shardIndex] = new Dictionary<TIndexKey, TKey[]>(writer.Shards[shardIndex], _definition.Comparer);
            writer.Owned[shardIndex] = true;
        }
        return writer.Shards[shardIndex];
    }

    private void AddToBucket(Writer writer, TIndexKey indexKey, TKey key)
    {
        var shard = GetWritableShard(writer, indexKey);
        var bucket = shard.TryGetValue(indexKey, out var existing) ? existing : EmptyBucket;
        var grown = new TKey[bucket.Length + 1];
        Array.Copy(bucket, grown, bucket.Length);
        grown[bucket.Length] = key;
        shard[indexKey] = grown;
    }

    private void RemoveFromBucket(Writer writer, TIndexKey indexKey, TKey key)
    {
        var shard = GetWritableShard(writer, indexKey);
        if (!shard.TryGetValue(indexKey, out var bucket))
        {
            return;
        }
        int position = Array.IndexOf(bucket, key);
        if (position < 0)
        {
            return;
        }
        if (bucket.Length == 1)
        {
            shard.Remove(indexKey);
            return;
        }
        var shrunk = new TKey[bucket.Length - 1];
        Array.Copy(bucket, shrunk, position);
        Array.Copy(bucket, position + 1, shrunk, position, bucket.Length - position - 1);
        shard[indexKey] = shrunk;
    }
}
