using System.Collections.Frozen;
using BenchmarkDotNet.Attributes;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// The batch-refresh scenario with a production-shaped row (strings, decimal, enum-ish int,
/// timestamps — reference-type payload) instead of synthetic <c>long → long</c>, and with both
/// batch key distributions: uniformly random (worst case for chunk copy-on-write) and clustered
/// (the common shape of real change feeds, where recently-active customers cluster).
/// </summary>
[MemoryDiagnoser]
public class RealisticRowBenchmarks
{
    public sealed record CustomerRow(
        long Id,
        string Name,
        string Email,
        string Region,
        decimal Balance,
        int Status,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    [Params(1_000_000)]
    public int N;

    [Params(5_000)]
    public int BatchSize;

    private static readonly string[] Regions = ["BR", "US", "DE", "JP", "IN", "UK", "FR", "AU"];

    private static CustomerRow MakeRow(long id, int generation) => new(
        id,
        $"Customer {id}",
        $"customer{id}@example.com",
        Regions[(int)(id % Regions.Length)],
        id * 1.37m + generation,
        (int)(id % 5),
        DateTime.UnixEpoch,
        DateTime.UnixEpoch.AddSeconds(generation));

    private SnapshotTable<long, CustomerRow> _table = null!;
    private Dictionary<long, CustomerRow> _plain = null!;
    private KeyValuePair<long, CustomerRow>[] _randomBatch = null!;
    private KeyValuePair<long, CustomerRow>[] _clusteredBatch = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rows = Enumerable.Range(0, N).Select(i => KeyValuePair.Create((long)i, MakeRow(i, 0)));
        _table = new SnapshotTable<long, CustomerRow>(capacityHint: N);
        _table.Reset(rows);
        _plain = new Dictionary<long, CustomerRow>(rows);

        var rng = new Random(42);
        _randomBatch = Enumerable.Range(0, BatchSize)
            .Select(_ => (long)rng.Next(N)).Distinct()
            .Select(k => KeyValuePair.Create(k, MakeRow(k, 1))).ToArray();
        // Clustered: contiguous key range, like "the customers active in the last half hour".
        long start = rng.Next(N - BatchSize);
        _clusteredBatch = Enumerable.Range(0, BatchSize)
            .Select(i => KeyValuePair.Create(start + i, MakeRow(start + i, 1))).ToArray();
    }

    [Benchmark(Baseline = true, Description = "SnapshotTable random 5k batch")]
    public object SnapshotTable_RandomBatch()
    {
        _table.ApplyChanges(_randomBatch);
        return _table;
    }

    [Benchmark(Description = "SnapshotTable clustered 5k batch")]
    public object SnapshotTable_ClusteredBatch()
    {
        _table.ApplyChanges(_clusteredBatch);
        return _table;
    }

    [Benchmark(Description = "Dictionary rebuild + swap")]
    public object Dictionary_Rebuild()
    {
        var next = new Dictionary<long, CustomerRow>(_plain);
        foreach (var kv in _randomBatch)
        {
            next[kv.Key] = kv.Value;
        }
        return next;
    }

    [Benchmark(Description = "FrozenDictionary rebuild")]
    public object FrozenDictionary_Rebuild()
    {
        var next = new Dictionary<long, CustomerRow>(_plain);
        foreach (var kv in _randomBatch)
        {
            next[kv.Key] = kv.Value;
        }
        return next.ToFrozenDictionary();
    }
}
