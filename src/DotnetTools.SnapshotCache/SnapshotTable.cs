using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotnetTools.SnapshotCache;

/// <summary>
/// A keyed in-memory table designed for the "large reference table, refreshed in batches" cache
/// pattern: up to hundreds of millions of rows, wait-free concurrent reads, and a periodic batch
/// of upserts/removes (e.g. customer changes applied every 30 seconds) that touches only a small
/// fraction of the rows.
///
/// <para><b>Read path.</b> Readers never take a lock. <see cref="TryGetValue"/> reads the current
/// snapshot with one volatile load; a snapshot obtained via <see cref="GetSnapshot"/> is fully
/// immutable and internally consistent, so a report can iterate it while updates keep landing.</para>
///
/// <para><b>Write path.</b> Writers are serialized. <see cref="ApplyChanges"/> builds the next
/// snapshot with copy-on-write and publishes it atomically:
/// <list type="bullet">
///   <item>Rows live in a <see cref="ChunkedImmutableList{T}"/> — only chunks (default ~4 KB) and
///   spine blocks containing modified rows are copied.</item>
///   <item>The key → row-index hash index is split into many small shards reached through a
///   two-level directory — only shards containing inserted/removed keys (and their directory
///   blocks) are cloned. In-place value updates never touch the index at all.</item>
/// </list>
/// A batch of B changes over N rows costs O(B · chunk) time and allocation, independent of N.
/// <b>Nothing in the structure — rows, chunks, spine, shards, directory, or per-batch
/// bookkeeping — ever allocates on the Large Object Heap, at any table size.</b></para>
///
/// <para><b>Removal</b> uses swap-remove: the last row is moved into the removed row's slot, so the
/// row store stays dense and iteration order is not stable across removals.</para>
/// </summary>
public sealed class SnapshotTable<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    // Index shards are kept small: cloning one on insert/remove is a few KB, and a shard's
    // internal Dictionary arrays stay far below the 85,000-byte LOH threshold even after growth.
    private const int TargetEntriesPerShard = 256;
    private const int MinShardCount = 8;
    private const int MaxShardCount = 1 << 19; // directory + bitsets stay sub-LOH

    // Directory geometry: 1024 shard references per directory block = 8 KB per block.
    private const int DirBlockShift = 10;
    private const int DirBlockLength = 1 << DirBlockShift;
    private const int DirBlockMask = DirBlockLength - 1;

    private readonly IEqualityComparer<TKey> _comparer;
    private readonly int _shardCount;
    private readonly int _shardShift; // shard = (hash * Fibonacci) >>> _shardShift
    private readonly int _presizeHint;
    private readonly ChunkedImmutableList<KeyValuePair<TKey, TValue>> _emptyRows;
    private readonly object _writeLock = new();
    private TableSnapshot _current;

    /// <param name="capacityHint">Expected number of rows; see <see cref="SnapshotTableOptions{TKey}.CapacityHint"/>.</param>
    /// <param name="comparer">Key comparer; defaults to <see cref="EqualityComparer{TKey}.Default"/>.</param>
    public SnapshotTable(int capacityHint = 0, IEqualityComparer<TKey>? comparer = null)
        : this(new SnapshotTableOptions<TKey> { CapacityHint = capacityHint, Comparer = comparer })
    {
    }

    public SnapshotTable(SnapshotTableOptions<TKey> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CapacityHint);
        _comparer = options.Comparer ?? EqualityComparer<TKey>.Default;
        _presizeHint = options.CapacityHint;
        _shardCount = (int)BitOperations.RoundUpToPowerOf2(
            (uint)Math.Clamp(options.CapacityHint / TargetEntriesPerShard, MinShardCount, MaxShardCount));
        _shardShift = 32 - BitOperations.Log2((uint)_shardCount);
        _emptyRows = options.ChunkRows > 0
            ? ChunkedImmutableList<KeyValuePair<TKey, TValue>>.EmptyWithChunkRows(options.ChunkRows)
            : ChunkedImmutableList<KeyValuePair<TKey, TValue>>.Empty;
        _current = new TableSnapshot(this, _emptyRows, NewEmptyDirectory());
    }

    // Shared read-only placeholder so the read path never null-checks.
    private static readonly Dictionary<TKey, int> EmptyShard = [];

    private Dictionary<TKey, int>[][] NewEmptyDirectory()
    {
        int blocks = (_shardCount + DirBlockMask) >> DirBlockShift;
        var dir = new Dictionary<TKey, int>[blocks][];
        for (int b = 0; b < blocks; b++)
        {
            var block = new Dictionary<TKey, int>[Math.Min(DirBlockLength, _shardCount)];
            Array.Fill(block, EmptyShard);
            dir[b] = block;
        }
        return dir;
    }

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
            var writer = new IndexWriter(this, snapshot.Directory);

            if (upserts is not null)
            {
                foreach (var (key, value) in upserts)
                {
                    int shardIndex = ShardOf(key);
                    if (writer.Read(shardIndex).TryGetValue(key, out int rowIndex))
                    {
                        // Value update: touches row chunks only — the index is left untouched.
                        rows[rowIndex] = new KeyValuePair<TKey, TValue>(key, value);
                    }
                    else
                    {
                        writer.Writable(shardIndex).Add(key, rows.Count);
                        rows.Add(new KeyValuePair<TKey, TValue>(key, value));
                    }
                }
            }

            if (removes is not null)
            {
                foreach (var key in removes)
                {
                    int shardIndex = ShardOf(key);
                    if (!writer.Read(shardIndex).TryGetValue(key, out int rowIndex))
                    {
                        continue;
                    }
                    writer.Writable(shardIndex).Remove(key);
                    int lastIndex = rows.Count - 1;
                    if (rowIndex != lastIndex)
                    {
                        // Swap-remove: relocate the last row into the vacated slot and re-point its key.
                        var moved = rows[lastIndex];
                        rows[rowIndex] = moved;
                        writer.Writable(ShardOf(moved.Key))[moved.Key] = rowIndex;
                    }
                    rows.RemoveLast();
                }
            }

            Volatile.Write(ref _current, new TableSnapshot(this, rows.ToImmutable(), writer.Directory));
        }
    }

    /// <summary>Atomically replaces the entire table content (a full reload). Shard dictionaries
    /// are pre-sized from the constructor's capacity hint to avoid growth churn.</summary>
    public void Reset(IEnumerable<KeyValuePair<TKey, TValue>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_writeLock)
        {
            var builder = _emptyRows.ToBuilder();
            var dir = NewEmptyDirectory();
            int presize = Math.Max(4, (int)Math.Min(int.MaxValue, (long)_presizeHint * 5 / (4 * _shardCount)));
            foreach (var (key, value) in rows)
            {
                int shardIndex = ShardOf(key);
                var block = dir[shardIndex >> DirBlockShift];
                var shard = block[shardIndex & DirBlockMask];
                if (ReferenceEquals(shard, EmptyShard))
                {
                    shard = new Dictionary<TKey, int>(presize, _comparer);
                    block[shardIndex & DirBlockMask] = shard;
                }
                ref var rowIndex = ref CollectionsMarshal.GetValueRefOrAddDefault(shard, key, out bool existed);
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
            Volatile.Write(ref _current, new TableSnapshot(this, builder.ToImmutable(), dir));
        }
    }

    /// <summary>Removes all rows.</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            Volatile.Write(ref _current, new TableSnapshot(this, _emptyRows, NewEmptyDirectory()));
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

        public readonly KeyValuePair<TKey, TValue> Current => _inner.Current;

        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => _inner.MoveNext();

        public void Reset() => _inner.Reset();

        public readonly void Dispose()
        {
        }
    }

    /// <summary>Copy-on-write access to the shard directory during one batch: directory blocks and
    /// shards are each cloned at most once, tracked in small bitsets (never LOH, at any scale).</summary>
    private struct IndexWriter
    {
        private readonly SnapshotTable<TKey, TValue> _table;
        private readonly ulong[] _blockOwned;
        private readonly ulong[]?[] _shardOwned; // per owned block: bit per shard (16 ulongs)
        public Dictionary<TKey, int>[][] Directory;

        public IndexWriter(SnapshotTable<TKey, TValue> table, Dictionary<TKey, int>[][] directory)
        {
            _table = table;
            Directory = (Dictionary<TKey, int>[][])directory.Clone();
            _blockOwned = new ulong[(Directory.Length + 63) >> 6];
            _shardOwned = new ulong[]?[Directory.Length];
        }

        public readonly Dictionary<TKey, int> Read(int shardIndex) =>
            Directory[shardIndex >> DirBlockShift][shardIndex & DirBlockMask];

        public Dictionary<TKey, int> Writable(int shardIndex)
        {
            int b = shardIndex >> DirBlockShift;
            int s = shardIndex & DirBlockMask;
            if ((_blockOwned[b >> 6] & (1UL << b)) == 0)
            {
                Directory[b] = (Dictionary<TKey, int>[])Directory[b].Clone();
                _blockOwned[b >> 6] |= 1UL << b;
                _shardOwned[b] = new ulong[DirBlockLength / 64];
            }
            var owned = _shardOwned[b]!;
            var shard = Directory[b][s];
            if ((owned[s >> 6] & (1UL << s)) == 0)
            {
                shard = new Dictionary<TKey, int>(shard, _table._comparer);
                Directory[b][s] = shard;
                owned[s >> 6] |= 1UL << s;
            }
            return shard;
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
        internal readonly Dictionary<TKey, int>[][] Directory;

        internal TableSnapshot(
            SnapshotTable<TKey, TValue> table,
            ChunkedImmutableList<KeyValuePair<TKey, TValue>> rows,
            Dictionary<TKey, int>[][] directory)
        {
            _table = table;
            Rows = rows;
            Directory = directory;
        }

        public int Count => Rows.Count;

        public bool TryGetValue(TKey key, out TValue value)
        {
            // Directory blocks and shards are never mutated after publication, so reading them
            // without a lock is safe.
            int shardIndex = _table.ShardOf(key);
            var shard = Directory[shardIndex >> DirBlockShift][shardIndex & DirBlockMask];
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
