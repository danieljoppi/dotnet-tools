using System.Collections.Frozen;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Models the real workload: a table of <see cref="N"/> rows kept in memory, refreshed every
/// 30 seconds with a batch of <see cref="BatchSize"/> customer changes, and read constantly.
/// </summary>
[MemoryDiagnoser]
public class BatchUpdateBenchmarks
{
    [Params(1_000_000)]
    public int N;

    [Params(5_000)]
    public int BatchSize;

    public record Row(long Id, string Name, decimal Balance, DateTime UpdatedAt);

    private KeyValuePair<long, Row>[] _batch = null!;
    private long[] _batchKeys = null!;

    private SnapshotTable<long, Row> _snapshotTable = null!;
    private ImmutableList<KeyValuePair<long, Row>> _immutableList = null!;
    private ImmutableArray<KeyValuePair<long, Row>> _immutableArray;
    private ImmutableDictionary<long, Row> _immutableDictionary = null!;
    private Dictionary<long, Row> _plainDictionary = null!;
    private FrozenDictionary<long, Row> _frozenDictionary = null!;

    private static Row MakeRow(long id) => new(id, $"customer-{id}", id * 1.5m, DateTime.UnixEpoch);

    [GlobalSetup]
    public void Setup()
    {
        var rows = Enumerable.Range(0, N).Select(i => KeyValuePair.Create((long)i, MakeRow(i)));

        _snapshotTable = new SnapshotTable<long, Row>(capacityHint: N);
        _snapshotTable.Reset(rows);

        _immutableList = ImmutableList.CreateRange(rows);
        _immutableArray = ImmutableArray.CreateRange(rows);
        _immutableDictionary = ImmutableDictionary.CreateRange(rows);
        _plainDictionary = new Dictionary<long, Row>(rows);
        _frozenDictionary = _plainDictionary.ToFrozenDictionary();

        // Spread the batch across the whole key space, like real customer changes.
        var rng = new Random(42);
        _batchKeys = Enumerable.Range(0, BatchSize).Select(_ => (long)rng.Next(N)).Distinct().ToArray();
        _batch = _batchKeys.Select(k => KeyValuePair.Create(k, MakeRow(-k))).ToArray();
    }

    // --- Applying one 30-second refresh batch ---

    [Benchmark(Baseline = true, Description = "SnapshotTable.ApplyChanges")]
    public object SnapshotTable_ApplyBatch()
    {
        _snapshotTable.ApplyChanges(_batch);
        return _snapshotTable;
    }

    [Benchmark(Description = "ImmutableDictionary.SetItems")]
    public object ImmutableDictionary_ApplyBatch() => _immutableDictionary.SetItems(_batch);

    [Benchmark(Description = "ImmutableList.SetItem xB (keyed rows)")]
    public object ImmutableList_ApplyBatch()
    {
        // ImmutableList has no key index; real code pairs it with a rebuilt lookup. We charge it
        // only the row replacement cost here (indexes assumed identical), which flatters it.
        var builder = _immutableList.ToBuilder();
        foreach (var kv in _batch)
        {
            builder[(int)kv.Key] = kv;
        }
        return builder.ToImmutable();
    }

    [Benchmark(Description = "ImmutableArray.SetItem xB via builder")]
    public object ImmutableArray_ApplyBatch()
    {
        // One full O(N) copy per batch — this is what SetItem/builder costs on a big array.
        var builder = _immutableArray.ToBuilder();
        foreach (var kv in _batch)
        {
            builder[(int)kv.Key] = kv;
        }
        return builder.ToImmutable();
    }

    [Benchmark(Description = "Dictionary rebuild + swap")]
    public object Dictionary_RebuildAndSwap()
    {
        var next = new Dictionary<long, Row>(_plainDictionary);
        foreach (var kv in _batch)
        {
            next[kv.Key] = kv.Value;
        }
        return next;
    }

    [Benchmark(Description = "FrozenDictionary rebuild")]
    public object FrozenDictionary_Rebuild()
    {
        var next = new Dictionary<long, Row>(_plainDictionary);
        foreach (var kv in _batch)
        {
            next[kv.Key] = kv.Value;
        }
        return next.ToFrozenDictionary();
    }
}

/// <summary>Point-lookup throughput against a table of <see cref="N"/> rows.</summary>
[MemoryDiagnoser]
public class ReadBenchmarks
{
    [Params(1_000_000)]
    public int N;

    private const int Lookups = 10_000;

    private long[] _keys = null!;
    private SnapshotTable<long, long> _snapshotTable = null!;
    private SnapshotTable<long, long>.TableSnapshot _snapshot = null!;
    private ImmutableList<long> _immutableList = null!;
    private ImmutableArray<long> _immutableArray;
    private ImmutableDictionary<long, long> _immutableDictionary = null!;
    private Dictionary<long, long> _plainDictionary = null!;
    private FrozenDictionary<long, long> _frozenDictionary = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rows = Enumerable.Range(0, N).Select(i => KeyValuePair.Create((long)i, (long)i * 3));
        _snapshotTable = new SnapshotTable<long, long>(capacityHint: N);
        _snapshotTable.Reset(rows);
        _snapshot = _snapshotTable.GetSnapshot();
        _immutableList = ImmutableList.CreateRange(Enumerable.Range(0, N).Select(i => (long)i * 3));
        _immutableArray = ImmutableArray.CreateRange(Enumerable.Range(0, N).Select(i => (long)i * 3));
        _immutableDictionary = ImmutableDictionary.CreateRange(rows);
        _plainDictionary = new Dictionary<long, long>(rows);
        _frozenDictionary = _plainDictionary.ToFrozenDictionary();

        var rng = new Random(7);
        _keys = Enumerable.Range(0, Lookups).Select(_ => (long)rng.Next(N)).ToArray();
    }

    [Benchmark(Baseline = true, Description = "SnapshotTable.TryGetValue x10k")]
    public long SnapshotTable_Lookups()
    {
        long sum = 0;
        foreach (long k in _keys)
        {
            _snapshotTable.TryGetValue(k, out long v);
            sum += v;
        }
        return sum;
    }

    [Benchmark(Description = "TableSnapshot.TryGetValue x10k")]
    public long Snapshot_Lookups()
    {
        long sum = 0;
        foreach (long k in _keys)
        {
            _snapshot.TryGetValue(k, out long v);
            sum += v;
        }
        return sum;
    }

    [Benchmark(Description = "Dictionary x10k")]
    public long Dictionary_Lookups()
    {
        long sum = 0;
        foreach (long k in _keys)
        {
            _plainDictionary.TryGetValue(k, out long v);
            sum += v;
        }
        return sum;
    }

    [Benchmark(Description = "FrozenDictionary x10k")]
    public long FrozenDictionary_Lookups()
    {
        long sum = 0;
        foreach (long k in _keys)
        {
            _frozenDictionary.TryGetValue(k, out long v);
            sum += v;
        }
        return sum;
    }

    [Benchmark(Description = "ImmutableDictionary x10k")]
    public long ImmutableDictionary_Lookups()
    {
        long sum = 0;
        foreach (long k in _keys)
        {
            _immutableDictionary.TryGetValue(k, out long v);
            sum += v;
        }
        return sum;
    }

    [Benchmark(Description = "ImmutableList[i] x10k")]
    public long ImmutableList_IndexLookups()
    {
        long sum = 0;
        foreach (long k in _keys)
        {
            sum += _immutableList[(int)k];
        }
        return sum;
    }

    [Benchmark(Description = "ImmutableArray[i] x10k")]
    public long ImmutableArray_IndexLookups()
    {
        long sum = 0;
        foreach (long k in _keys)
        {
            sum += _immutableArray[(int)k];
        }
        return sum;
    }
}

/// <summary>Cost of the initial full load of <see cref="N"/> rows.</summary>
[MemoryDiagnoser]
public class InitialLoadBenchmarks
{
    [Params(1_000_000)]
    public int N;

    private KeyValuePair<long, long>[] _rows = null!;

    [GlobalSetup]
    public void Setup() =>
        _rows = Enumerable.Range(0, N).Select(i => KeyValuePair.Create((long)i, (long)i)).ToArray();

    [Benchmark(Baseline = true, Description = "SnapshotTable.Reset")]
    public object SnapshotTable_Load()
    {
        var table = new SnapshotTable<long, long>(capacityHint: N);
        table.Reset(_rows);
        return table;
    }

    [Benchmark(Description = "ImmutableList.CreateRange")]
    public object ImmutableList_Load() => ImmutableList.CreateRange(_rows);

    [Benchmark(Description = "ImmutableDictionary.CreateRange")]
    public object ImmutableDictionary_Load() => ImmutableDictionary.CreateRange(_rows);

    [Benchmark(Description = "FrozenDictionary.ToFrozenDictionary")]
    public object FrozenDictionary_Load() => _rows.ToFrozenDictionary();

    [Benchmark(Description = "Dictionary")]
    public object Dictionary_Load() => new Dictionary<long, long>(_rows);
}
