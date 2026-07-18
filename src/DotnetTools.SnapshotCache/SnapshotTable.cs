using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotnetTools.SnapshotCache;

/// <summary>
/// A keyed in-memory table designed for the "large reference table, refreshed in batches" cache
/// pattern: millions of rows, wait-free concurrent reads, and a periodic batch of upserts/removes
/// (e.g. customer changes applied every 30 seconds) that touches only a small fraction of the rows.
///
/// <para><b>Read path.</b> Readers never take a lock. <see cref="TryGetValue"/> reads the current
/// snapshot with one volatile load; a snapshot obtained via <see cref="GetSnapshot"/> is fully
/// immutable and internally consistent, so a report can iterate it while updates keep landing.</para>
///
/// <para><b>Write path.</b> Writers are serialized. <see cref="ApplyChanges"/> builds the next
/// snapshot with copy-on-write at two granularities and publishes it atomically:
/// <list type="bullet">
///   <item>Rows live in a <see cref="ChunkedImmutableList{T}"/> — only chunks containing modified
///   rows are copied (LOH-free, O(changed-chunks) instead of O(n)).</item>
///   <item>The key → row-index hash index is split into many small shards, each safely below the
///   LOH threshold — only shards containing modified keys are cloned.</item>
/// </list>
/// A batch of B changes over N rows costs O(B · chunk) time and allocation, independent of N,
/// instead of the O(n) full copy of <c>ImmutableArray</c> / <c>FrozenDictionary</c> rebuilds or the
/// O(B log n) many-small-nodes churn of <c>ImmutableList</c>/<c>ImmutableDictionary</c>.</para>
///
/// <para><b>Removal</b> uses swap-remove: the last row is moved into the removed row's slot, so the
/// row store stays dense and iteration order is not stable across removals.</para>
/// </summary>
public sealed class SnapshotTable<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    // Target entries per index shard, chosen so each shard's Dictionary internal arrays stay well
    // below the 85,000-byte LOH threshold (entry structs are ~24-32 bytes for common key types).
    private const int TargetEntriesPerShard = 2048;
    private const int MinShardCount = 8;
    private const int MaxShardCount = 1 << 15;

    private readonly IEqualityComparer<TKey> _comparer;
    private readonly int _shardShift; // shard = (hash * Fibonacci) >>> _shardShift
    private readonly object _writeLock = new();
    private TableSnapshot _current;

    /// <param name="capacityHint">Expected number of rows; used to pick the index shard count.
    /// The table grows fine past the hint, but a good hint keeps every shard below the LOH threshold.</param>
    /// <param name="comparer">Key comparer; defaults to <see cref="EqualityComparer{TKey}.Default"/>.</param>
    public SnapshotTable(int capacityHint = 0, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityHint);
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
        int shardCount = (int)BitOperations.RoundUpToPowerOf2(
            (uint)Math.Clamp(capacityHint / TargetEntriesPerShard, MinShardCount, MaxShardCount));
        _shardShift = 32 - BitOperations.Log2((uint)shardCount);
        var shards = new Dictionary<TKey, int>[shardCount];
        Array.Fill(shards, EmptyShard);
        _current = new TableSnapshot(this, ChunkedImmutableList<KeyValuePair<TKey, TValue>>.Empty, shards);
    }

    // Shared read-only placeholder so the read path never null-checks.
    private static readonly Dictionary<TKey, int> EmptyShard = [];

    /// <summary>Number of rows in the current snapshot.</summary>
    public int Count => Volatile.Read(ref _current).Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ShardOf(TKey key) =>
        (int)((uint)(_comparer.GetHashCode(key) * -1640531527 /* 0x9E3779B9, Fibonacci hashing */) >> _shardShift);

    /// <summary>Wait-free point lookup against the current snapshot.</summary>
    public bool TryGetValue(TKey key, out TValue value) =>
        Volatile.Read(ref _current).TryGetValue(key, out value);

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    public TValue this[TKey key] =>
        TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"The key '{key}' was not found.");

    /// <summary>Returns the current immutable snapshot. Use it when several lookups or a full
    /// iteration must observe one consistent version of the table.</summary>
    public TableSnapshot GetSnapshot() => Volatile.Read(ref _current);

    /// <summary>Inserts or replaces a single row. For periodic refreshes prefer
    /// <see cref="ApplyChanges"/>, which copies each touched chunk/shard once per batch.</summary>
    public void Upsert(TKey key, TValue value) =>
        ApplyChanges([new KeyValuePair<TKey, TValue>(key, value)], null);

    /// <summary>Removes a single row; returns false if the key was not present.</summary>
    public bool Remove(TKey key)
    {
        lock (_writeLock) // Monitor is reentrant, so the nested ApplyChanges lock is fine.
        {
            if (!_current.ContainsKey(key))
            {
                return false;
            }
            ApplyChanges(null, [key]);
            return true;
        }
    }

    /// <summary>Applies a batch of upserts and removes as one atomic snapshot transition.
    /// Readers see either the entire batch or none of it. Removes are applied after upserts;
    /// within the upserts, the last write for a key wins.</summary>
    public void ApplyChanges(
        IEnumerable<KeyValuePair<TKey, TValue>>? upserts,
        IEnumerable<TKey>? removes = null)
    {
        lock (_writeLock)
        {
            var snapshot = _current;
            var rows = snapshot.Rows.ToBuilder();
            // Copy-on-write over the shard spine: shards are cloned lazily, at most once per batch.
            var shards = (Dictionary<TKey, int>[])snapshot.Shards.Clone();
            var ownedShards = new bool[shards.Length];

            Dictionary<TKey, int> GetWritableShard(int shardIndex)
            {
                var shard = shards[shardIndex];
                if (!ownedShards[shardIndex])
                {
                    shard = new Dictionary<TKey, int>(shard, _comparer);
                    shards[shardIndex] = shard;
                    ownedShards[shardIndex] = true;
                }
                return shard;
            }

            if (upserts is not null)
            {
                foreach (var (key, value) in upserts)
                {
                    int shardIndex = ShardOf(key);
                    if (shards[shardIndex].TryGetValue(key, out int rowIndex))
                    {
                        rows[rowIndex] = new KeyValuePair<TKey, TValue>(key, value);
                    }
                    else
                    {
                        GetWritableShard(shardIndex).Add(key, rows.Count);
                        rows.Add(new KeyValuePair<TKey, TValue>(key, value));
                    }
                }
            }

            if (removes is not null)
            {
                foreach (var key in removes)
                {
                    int shardIndex = ShardOf(key);
                    if (!shards[shardIndex].TryGetValue(key, out int rowIndex))
                    {
                        continue;
                    }
                    GetWritableShard(shardIndex).Remove(key);
                    int lastIndex = rows.Count - 1;
                    if (rowIndex != lastIndex)
                    {
                        // Swap-remove: relocate the last row into the vacated slot and re-point its key.
                        var moved = rows[lastIndex];
                        rows[rowIndex] = moved;
                        GetWritableShard(ShardOf(moved.Key))[moved.Key] = rowIndex;
                    }
                    rows.RemoveLast();
                }
            }

            Volatile.Write(ref _current, new TableSnapshot(this, rows.ToImmutable(), shards));
        }
    }

    /// <summary>Atomically replaces the entire table content (a full reload).</summary>
    public void Reset(IEnumerable<KeyValuePair<TKey, TValue>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_writeLock)
        {
            var builder = ChunkedImmutableList<KeyValuePair<TKey, TValue>>.Empty.ToBuilder();
            var shards = new Dictionary<TKey, int>[_current.Shards.Length];
            Array.Fill(shards, EmptyShard);
            var owned = new bool[shards.Length];
            foreach (var (key, value) in rows)
            {
                int shardIndex = ShardOf(key);
                if (!owned[shardIndex])
                {
                    shards[shardIndex] = new Dictionary<TKey, int>(_comparer);
                    owned[shardIndex] = true;
                }
                ref var rowIndex = ref System.Runtime.InteropServices.CollectionsMarshal
                    .GetValueRefOrAddDefault(shards[shardIndex], key, out bool existed);
                if (existed)
                {
                    builder[rowIndex] = new KeyValuePair<TKey, TValue>(key, value);
                }
                else
                {
                    rowIndex = builder.Count;
                    builder.Add(new KeyValuePair<TKey, TValue>(key, value));
                }
            }
            Volatile.Write(ref _current, new TableSnapshot(this, builder.ToImmutable(), shards));
        }
    }

    /// <summary>Removes all rows.</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            var shards = new Dictionary<TKey, int>[_current.Shards.Length];
            Array.Fill(shards, EmptyShard);
            Volatile.Write(ref _current,
                new TableSnapshot(this, ChunkedImmutableList<KeyValuePair<TKey, TValue>>.Empty, shards));
        }
    }

    public Enumerator GetEnumerator() => new(GetSnapshot());

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
        GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private ChunkedImmutableList<KeyValuePair<TKey, TValue>>.Enumerator _inner;

        internal Enumerator(TableSnapshot snapshot) => _inner = snapshot.Rows.GetEnumerator();

        public KeyValuePair<TKey, TValue> Current => _inner.Current;

        object IEnumerator.Current => Current;

        public bool MoveNext() => _inner.MoveNext();

        public void Reset() => _inner.Reset();

        public readonly void Dispose()
        {
        }
    }

    /// <summary>
    /// A fully immutable, internally consistent point-in-time view of the table. Snapshots are
    /// cheap to hold (they share all unchanged chunks and shards with newer versions) and safe to
    /// read from any thread without synchronization.
    /// </summary>
    public sealed class TableSnapshot : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
    {
        private readonly SnapshotTable<TKey, TValue> _table;

        internal readonly ChunkedImmutableList<KeyValuePair<TKey, TValue>> Rows;
        internal readonly Dictionary<TKey, int>[] Shards;

        internal TableSnapshot(
            SnapshotTable<TKey, TValue> table,
            ChunkedImmutableList<KeyValuePair<TKey, TValue>> rows,
            Dictionary<TKey, int>[] shards)
        {
            _table = table;
            Rows = rows;
            Shards = shards;
        }

        public int Count => Rows.Count;

        public bool TryGetValue(TKey key, out TValue value)
        {
            // Shards are never mutated after publication, so reading them without a lock is safe.
            var shard = Shards[_table.ShardOf(key)];
            if (shard.TryGetValue(key, out int rowIndex))
            {
                value = Rows[rowIndex].Value;
                return true;
            }
            value = default!;
            return false;
        }

        public bool ContainsKey(TKey key) => TryGetValue(key, out _);

        public Enumerator GetEnumerator() => new(this);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
            GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
