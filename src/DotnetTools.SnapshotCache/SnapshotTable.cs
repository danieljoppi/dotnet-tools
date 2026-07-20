using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;

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
///   <item>Rows live in a <see cref="ChunkedImmutableList{T}"/> — only chunks and spine blocks
///   containing modified rows are copied.</item>
///   <item>The key → row-index hash index is split into many small <see cref="ShardMap{TKey}"/>
///   shards reached through a two-level directory — only shards containing inserted/removed keys
///   (and their directory blocks) are cloned. In-place value updates never touch the index.</item>
///   <item>Registered <see cref="CreateIndex">secondary indexes</see> are maintained inside the
///   same atomic transition.</item>
/// </list>
/// A batch of B changes over N rows costs O(B · chunk) time and allocation, independent of N.
/// <b>Nothing in the structure — rows, chunks, spine, shards, directory, indexes, or per-batch
/// bookkeeping — ever allocates on the Large Object Heap, at any table size.</b></para>
///
/// <para><b>Removal</b> uses swap-remove: the last row is moved into the removed row's slot, so the
/// row store stays dense and iteration order is not stable across removals.</para>
/// </summary>
public sealed class SnapshotTable<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    // Index shards are kept small: cloning one on insert/remove is a few KB, and a shard's
    // internal arrays stay far below the 85,000-byte LOH threshold even after growth.
    private const int TargetEntriesPerShard = 256;
    private const int MinShardCount = 8;
    private const int MaxShardCount = 1 << 19; // directory + bitsets stay sub-LOH

    // Directory geometry: 1024 shard references per directory block = 8 KB per block.
    private const int DirBlockShift = 10;
    private const int DirBlockLength = 1 << DirBlockShift;
    private const int DirBlockMask = DirBlockLength - 1;

    private readonly IEqualityComparer<TKey> _comparer;
    private readonly bool _defaultComparer; // value-type TKey + default comparer → devirtualized hash
    private readonly int _shardCount;
    private readonly int _shardShift; // shard = (hash * Fibonacci) >>> _shardShift
    private readonly int _presizeHint;
    private readonly ChunkedImmutableList<KeyValuePair<TKey, TValue>> _emptyRows;
    private readonly ShardMap<TKey> _emptyShard; // shared placeholder; never mutated (writers clone)
    private readonly List<Func<IndexState<TKey, TValue>>> _indexFactories = [];
    private readonly object _writeLock = new();
    private TableSnapshot _current;

    /// <summary>Raised inside the write lock immediately after a new snapshot is published.
    /// Handlers must be fast; heavy work should be queued elsewhere.</summary>
    public event Action<SnapshotChangedEventArgs>? SnapshotChanged;

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
        _defaultComparer = typeof(TKey).IsValueType && ReferenceEquals(_comparer, EqualityComparer<TKey>.Default);
        _presizeHint = options.CapacityHint;
        _shardCount = (int)BitOperations.RoundUpToPowerOf2(
            (uint)Math.Clamp(options.CapacityHint / TargetEntriesPerShard, MinShardCount, MaxShardCount));
        _shardShift = 32 - BitOperations.Log2((uint)_shardCount);
        _emptyShard = new ShardMap<TKey>(_comparer);
        // Adaptive default chunk size: for tables up to a few million rows a typical refresh batch
        // touches a large fraction of the chunks, so bigger chunks (fewer, larger memcpys) win;
        // for huge tables the batch is sparse relative to the chunk count, so small chunks
        // minimize copy-on-write volume. Both stay far below the LOH threshold.
        _emptyRows = options.ChunkRows > 0
            ? ChunkedImmutableList<KeyValuePair<TKey, TValue>>.EmptyWithChunkRows(options.ChunkRows)
            : ChunkedImmutableList<KeyValuePair<TKey, TValue>>.EmptyWithTargetBytes(
                options.CapacityHint >= 8_000_000 ? 4 * 1024 : 64 * 1024);
        _current = new TableSnapshot(this, _emptyRows, NewEmptyDirectory(), []);
    }

    private ShardMap<TKey>[][] NewEmptyDirectory()
    {
        int blocks = (_shardCount + DirBlockMask) >> DirBlockShift;
        var dir = new ShardMap<TKey>[blocks][];
        for (int b = 0; b < blocks; b++)
        {
            var block = new ShardMap<TKey>[Math.Min(DirBlockLength, _shardCount)];
            Array.Fill(block, _emptyShard);
            dir[b] = block;
        }
        return dir;
    }

    /// <summary>Number of rows in the current snapshot.</summary>
    public int Count => Volatile.Read(ref _current).Count;

    // Same devirtualization trick as ShardMap: for value-type keys with the default comparer the
    // EqualityComparer<TKey>.Default intrinsic inlines the hash, skipping interface dispatch on
    // the hot path (measurable for composite keys like (long, long)).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ShardOf(TKey key)
    {
        int hash = typeof(TKey).IsValueType && _defaultComparer
            ? EqualityComparer<TKey>.Default.GetHashCode(key)
            : _comparer.GetHashCode(key);
        return (int)((uint)(hash * -1640531527 /* 0x9E3779B9, Fibonacci hashing */) >> _shardShift);
    }

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

    /// <summary>Registers a secondary index (index key → primary keys), maintained atomically with
    /// every batch. Must be called before any rows are loaded. Suited to moderate-cardinality
    /// attributes with buckets up to the low thousands of keys.</summary>
    public TableIndex<TKey, TValue, TIndexKey> CreateIndex<TIndexKey>(
        Func<TKey, TValue, TIndexKey> selector,
        IEqualityComparer<TIndexKey>? comparer = null,
        int shardCount = 1024)
        where TIndexKey : notnull
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (shardCount < 1 || (shardCount & (shardCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shardCount), shardCount, "Shard count must be a power of two.");
        }
        lock (_writeLock)
        {
            if (_current.Count > 0)
            {
                throw new InvalidOperationException("Secondary indexes must be registered before rows are loaded.");
            }
            var definition = new TableIndex<TKey, TValue, TIndexKey>(
                _indexFactories.Count, selector, comparer ?? EqualityComparer<TIndexKey>.Default);
            _indexFactories.Add(() => new IndexState<TKey, TValue, TIndexKey>(definition, shardCount));
            var indexes = new IndexState<TKey, TValue>[_indexFactories.Count];
            for (int i = 0; i < indexes.Length; i++)
            {
                indexes[i] = _indexFactories[i]();
            }
            Volatile.Write(ref _current, new TableSnapshot(this, _current.Rows, _current.Directory, indexes));
            return definition;
        }
    }

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
            var indexes = snapshot.Indexes;
            object[]? indexWriters = null;
            if (indexes.Length > 0)
            {
                indexWriters = new object[indexes.Length];
                for (int i = 0; i < indexes.Length; i++)
                {
                    indexWriters[i] = indexes[i].CreateWriter();
                }
            }
            bool notify = SnapshotChanged is not null;
            List<TKey>? upsertedKeys = notify ? [] : null;
            List<TKey>? removedKeys = notify ? [] : null;

            if (upserts is not null)
            {
                foreach (var (key, value) in upserts)
                {
                    int shardIndex = ShardOf(key);
                    if (writer.Read(shardIndex).TryGetValue(key, out int rowIndex))
                    {
                        // Value update: touches row chunks only — the primary index is untouched.
                        if (indexWriters is not null)
                        {
                            var oldValue = rows[rowIndex].Value;
                            for (int i = 0; i < indexes.Length; i++)
                            {
                                indexes[i].Apply(indexWriters[i], key, true, oldValue, true, value);
                            }
                        }
                        rows[rowIndex] = new KeyValuePair<TKey, TValue>(key, value);
                    }
                    else
                    {
                        writer.Writable(shardIndex).Set(key, rows.Count);
                        rows.Add(new KeyValuePair<TKey, TValue>(key, value));
                        if (indexWriters is not null)
                        {
                            for (int i = 0; i < indexes.Length; i++)
                            {
                                indexes[i].Apply(indexWriters[i], key, false, default, true, value);
                            }
                        }
                    }
                    upsertedKeys?.Add(key);
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
                    var oldValue = rows[rowIndex].Value;
                    writer.Writable(shardIndex).Remove(key);
                    int lastIndex = rows.Count - 1;
                    if (rowIndex != lastIndex)
                    {
                        // Swap-remove: relocate the last row into the vacated slot and re-point its
                        // key. Secondary indexes map keys, not row slots, so they are unaffected.
                        var moved = rows[lastIndex];
                        rows[rowIndex] = moved;
                        writer.Writable(ShardOf(moved.Key)).Set(moved.Key, rowIndex);
                    }
                    rows.RemoveLast();
                    if (indexWriters is not null)
                    {
                        for (int i = 0; i < indexes.Length; i++)
                        {
                            indexes[i].Apply(indexWriters[i], key, true, oldValue, false, default);
                        }
                    }
                    removedKeys?.Add(key);
                }
            }

            var newIndexes = indexes;
            if (indexWriters is not null)
            {
                newIndexes = new IndexState<TKey, TValue>[indexes.Length];
                for (int i = 0; i < indexes.Length; i++)
                {
                    newIndexes[i] = indexes[i].Freeze(indexWriters[i]);
                }
            }
            var next = new TableSnapshot(this, rows.ToImmutable(), writer.Directory, newIndexes);
            Volatile.Write(ref _current, next);
            if (notify)
            {
                SnapshotChanged?.Invoke(new SnapshotChangedEventArgs(next, upsertedKeys!, removedKeys!, isFullReload: false));
            }
        }
    }

    /// <summary>Atomically replaces the entire table content (a full reload). Shards are pre-sized
    /// from the constructor's capacity hint to avoid growth churn.</summary>
    public void Reset(IEnumerable<KeyValuePair<TKey, TValue>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_writeLock)
        {
            var builder = _emptyRows.ToBuilder();
            var dir = NewEmptyDirectory();
            var indexes = NewEmptyIndexes();
            var indexWriters = CreateIndexWriters(indexes);
            int presize = ShardPresizeCapacity();
            foreach (var (key, value) in rows)
            {
                int shardIndex = ShardOf(key);
                var block = dir[shardIndex >> DirBlockShift];
                var shard = block[shardIndex & DirBlockMask];
                if (ReferenceEquals(shard, _emptyShard))
                {
                    shard = new ShardMap<TKey>(_comparer, presize);
                    block[shardIndex & DirBlockMask] = shard;
                }
                if (shard.TryGetValue(key, out int rowIndex))
                {
                    if (indexWriters is not null)
                    {
                        var oldValue = builder[rowIndex].Value;
                        ApplyToIndexes(indexes, indexWriters, key, true, oldValue, true, value);
                    }
                    builder[rowIndex] = new KeyValuePair<TKey, TValue>(key, value);
                }
                else
                {
                    shard.Set(key, builder.Count);
                    builder.Add(new KeyValuePair<TKey, TValue>(key, value));
                    if (indexWriters is not null)
                    {
                        ApplyToIndexes(indexes, indexWriters, key, false, default, true, value);
                    }
                }
            }
            PublishReload(builder.ToImmutable(), dir, indexes, indexWriters);
        }
    }

    /// <summary>
    /// Like <see cref="Reset"/> but builds the key index on multiple cores. <b>Requires unique
    /// keys</b> in <paramref name="rows"/> (the common case: a database primary key); duplicate
    /// keys are detected and rejected with an exception. At 100M rows this cuts the load time
    /// roughly by the core count.
    /// </summary>
    public void ResetParallel(IEnumerable<KeyValuePair<TKey, TValue>> rows, int degreeOfParallelism = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int workers = degreeOfParallelism > 0 ? degreeOfParallelism : Environment.ProcessorCount;
        lock (_writeLock)
        {
            // Phase 1 (sequential): materialize rows into chunks; this is a fast streaming append.
            var builder = _emptyRows.ToBuilder();
            foreach (var row in rows)
            {
                builder.Add(row);
            }
            var rowList = builder.ToImmutable();

            // Phase 2 (parallel): each worker indexes the shards it owns (shard % workers == w),
            // scanning the chunked row list sequentially. Shard ownership is disjoint → no locks.
            var dir = NewEmptyDirectory();
            int presize = ShardPresizeCapacity();
            Parallel.For(0, workers, w =>
            {
                int rowIndex = 0;
                foreach (var (key, _) in rowList)
                {
                    int shardIndex = ShardOf(key);
                    if (shardIndex % workers == w)
                    {
                        var block = dir[shardIndex >> DirBlockShift];
                        var shard = block[shardIndex & DirBlockMask];
                        if (ReferenceEquals(shard, _emptyShard))
                        {
                            shard = new ShardMap<TKey>(_comparer, presize);
                            block[shardIndex & DirBlockMask] = shard;
                        }
                        shard.Set(key, rowIndex);
                    }
                    rowIndex++;
                }
            });

            long indexed = 0;
            foreach (var block in dir)
            {
                foreach (var shard in block)
                {
                    indexed += shard.Count;
                }
            }
            if (indexed != rowList.Count)
            {
                throw new InvalidOperationException(
                    $"ResetParallel requires unique keys: {rowList.Count} rows produced {indexed} index entries. " +
                    "Use Reset(...) for streams that may contain duplicate keys.");
            }

            // Secondary indexes (sequential; typically far smaller than the primary index).
            var indexes = NewEmptyIndexes();
            var indexWriters = CreateIndexWriters(indexes);
            if (indexWriters is not null)
            {
                foreach (var (key, value) in rowList)
                {
                    ApplyToIndexes(indexes, indexWriters, key, false, default, true, value);
                }
            }
            PublishReload(rowList, dir, indexes, indexWriters);
        }
    }

    private int ShardPresizeCapacity() =>
        Math.Max(8, (int)Math.Min(int.MaxValue, (long)_presizeHint * 3 / (2 * _shardCount)));

    private IndexState<TKey, TValue>[] NewEmptyIndexes()
    {
        if (_indexFactories.Count == 0)
        {
            return [];
        }
        var indexes = new IndexState<TKey, TValue>[_indexFactories.Count];
        for (int i = 0; i < indexes.Length; i++)
        {
            indexes[i] = _indexFactories[i]();
        }
        return indexes;
    }

    private static object[]? CreateIndexWriters(IndexState<TKey, TValue>[] indexes)
    {
        if (indexes.Length == 0)
        {
            return null;
        }
        var writers = new object[indexes.Length];
        for (int i = 0; i < indexes.Length; i++)
        {
            writers[i] = indexes[i].CreateWriter();
        }
        return writers;
    }

    private static void ApplyToIndexes(
        IndexState<TKey, TValue>[] indexes, object[] writers,
        TKey key, bool hadOld, TValue? oldValue, bool hasNew, TValue? newValue)
    {
        for (int i = 0; i < indexes.Length; i++)
        {
            indexes[i].Apply(writers[i], key, hadOld, oldValue, hasNew, newValue);
        }
    }

    private void PublishReload(
        ChunkedImmutableList<KeyValuePair<TKey, TValue>> rows,
        ShardMap<TKey>[][] dir,
        IndexState<TKey, TValue>[] indexes,
        object[]? indexWriters)
    {
        if (indexWriters is not null)
        {
            for (int i = 0; i < indexes.Length; i++)
            {
                indexes[i] = indexes[i].Freeze(indexWriters[i]);
            }
        }
        var next = new TableSnapshot(this, rows, dir, indexes);
        Volatile.Write(ref _current, next);
        SnapshotChanged?.Invoke(new SnapshotChangedEventArgs(next, [], [], isFullReload: true));
    }

    /// <summary>Removes all rows.</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            PublishReload(_emptyRows, NewEmptyDirectory(), NewEmptyIndexes(), null);
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

    /// <summary>Payload of <see cref="SnapshotChanged"/>: the freshly published snapshot plus the
    /// keys the batch upserted/removed (empty for full reloads, which set <see cref="IsFullReload"/>).</summary>
    public sealed class SnapshotChangedEventArgs
    {
        public TableSnapshot Snapshot { get; }
        public IReadOnlyList<TKey> UpsertedKeys { get; }
        public IReadOnlyList<TKey> RemovedKeys { get; }
        public bool IsFullReload { get; }

        internal SnapshotChangedEventArgs(
            TableSnapshot snapshot, IReadOnlyList<TKey> upsertedKeys, IReadOnlyList<TKey> removedKeys, bool isFullReload)
        {
            Snapshot = snapshot;
            UpsertedKeys = upsertedKeys;
            RemovedKeys = removedKeys;
            IsFullReload = isFullReload;
        }
    }

    /// <summary>Copy-on-write access to the shard directory during one batch: directory blocks and
    /// shards are each cloned at most once, tracked in small bitsets (never LOH, at any scale).</summary>
    private struct IndexWriter
    {
        private readonly SnapshotTable<TKey, TValue> _table;
        private readonly ulong[] _blockOwned;
        private readonly ulong[]?[] _shardOwned; // per owned block: bit per shard (16 ulongs)
        public ShardMap<TKey>[][] Directory;

        public IndexWriter(SnapshotTable<TKey, TValue> table, ShardMap<TKey>[][] directory)
        {
            _table = table;
            Directory = (ShardMap<TKey>[][])directory.Clone();
            _blockOwned = new ulong[(Directory.Length + 63) >> 6];
            _shardOwned = new ulong[]?[Directory.Length];
        }

        public readonly ShardMap<TKey> Read(int shardIndex) =>
            Directory[shardIndex >> DirBlockShift][shardIndex & DirBlockMask];

        public ShardMap<TKey> Writable(int shardIndex)
        {
            int b = shardIndex >> DirBlockShift;
            int s = shardIndex & DirBlockMask;
            if ((_blockOwned[b >> 6] & (1UL << b)) == 0)
            {
                Directory[b] = (ShardMap<TKey>[])Directory[b].Clone();
                _blockOwned[b >> 6] |= 1UL << b;
                _shardOwned[b] = new ulong[DirBlockLength / 64];
            }
            var owned = _shardOwned[b]!;
            var shard = Directory[b][s];
            if ((owned[s >> 6] & (1UL << s)) == 0)
            {
                shard = shard.Clone();
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
        internal readonly ShardMap<TKey>[][] Directory;
        internal readonly IndexState<TKey, TValue>[] Indexes;

        internal TableSnapshot(
            SnapshotTable<TKey, TValue> table,
            ChunkedImmutableList<KeyValuePair<TKey, TValue>> rows,
            ShardMap<TKey>[][] directory,
            IndexState<TKey, TValue>[] indexes)
        {
            _table = table;
            Rows = rows;
            Directory = directory;
            Indexes = indexes;
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

        /// <summary>Primary keys currently in <paramref name="indexKey"/>'s bucket of the given
        /// secondary index, consistent with this snapshot. Returns an empty list for unknown keys.</summary>
        public IReadOnlyList<TKey> Lookup<TIndexKey>(TableIndex<TKey, TValue, TIndexKey> index, TIndexKey indexKey)
            where TIndexKey : notnull
        {
            ArgumentNullException.ThrowIfNull(index);
            return ((IndexState<TKey, TValue, TIndexKey>)Indexes[index.Ordinal]).Lookup(indexKey);
        }

        /// <summary>Rows in <paramref name="indexKey"/>'s bucket, resolved against this snapshot.</summary>
        public IEnumerable<KeyValuePair<TKey, TValue>> LookupRows<TIndexKey>(
            TableIndex<TKey, TValue, TIndexKey> index, TIndexKey indexKey)
            where TIndexKey : notnull
        {
            foreach (var key in Lookup(index, indexKey))
            {
                if (TryGetValue(key, out var value))
                {
                    yield return new KeyValuePair<TKey, TValue>(key, value);
                }
            }
        }

        public Enumerator GetEnumerator() => new(this);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
            GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
