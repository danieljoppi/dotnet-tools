using System.Collections.Frozen;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Workload A extension of <see cref="BatchUpdateBenchmarks"/> (which pins N=1M, B=5k, no removes):
/// batch size swept over 1k / 10k / 50k with 0% or 10% of the batch as removes, on a mid-width row.
/// The remove keys are a fixed subset of the upsert keys, so state is stable across invocations:
/// every batch re-inserts what the previous one removed, then removes the same 10% again —
/// a warm ApplyChanges steady state. <c>HoldSnapshot</c> measures the same apply while the
/// previous snapshot is pinned across the batch (the retained-memory side is in the
/// <c>--bucket-loh</c> study; structural sharing keeps the timing identical).
/// </summary>
[MemoryDiagnoser]
public class UniqueKeyBatchBenchmarks
{
    public sealed record Row(
        long Id, int Status, int Version, string Name, string Code, decimal Amount, DateTime UpdatedAt);

    private static Row MakeRow(long id, int version) => new(
        id, (int)(id % 5), version, $"row-{id}", $"code-{id % 1000}", id * 1.37m + version, DateTime.UnixEpoch.AddSeconds(version));

    [Params(1_000_000)]
    public int N;

    [Params(1_000, 10_000, 50_000)]
    public int BatchSize;

    [Params(0, 10)]
    public int RemovePercent;

    private SnapshotTable<long, Row> _table = null!;
    private Dictionary<long, Row> _plain = null!;
    private ImmutableDictionary<long, Row> _immutable = null!;
    private KeyValuePair<long, Row>[] _batch = null!;
    private long[] _removes = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rows = Enumerable.Range(0, N).Select(i => KeyValuePair.Create((long)i, MakeRow(i, 0)));
        _table = new SnapshotTable<long, Row>(capacityHint: N);
        _table.Reset(rows);
        _plain = new Dictionary<long, Row>(rows);
        _immutable = ImmutableDictionary.CreateRange(rows);

        var rng = new Random(42);
        var keys = new HashSet<long>();
        while (keys.Count < BatchSize)
        {
            keys.Add(rng.Next(N));
        }
        _batch = keys.Select(k => KeyValuePair.Create(k, MakeRow(k, 1))).ToArray();
        _removes = keys.Take(BatchSize * RemovePercent / 100).ToArray();
    }

    [Benchmark(Baseline = true, Description = "SnapshotTable.ApplyChanges")]
    public int SnapshotTable_ApplyChanges()
    {
        _table.ApplyChanges(_batch, _removes);
        return _table.Count;
    }

    [Benchmark(Description = "SnapshotTable.ApplyChanges + held snapshot")]
    public int SnapshotTable_HoldSnapshotAcrossBatch()
    {
        var previous = _table.GetSnapshot();
        _table.ApplyChanges(_batch, _removes);
        return previous.Count;
    }

    [Benchmark(Description = "Dictionary rebuild + swap")]
    public object Dictionary_RebuildAndSwap()
    {
        var next = new Dictionary<long, Row>(_plain);
        foreach (var kv in _batch)
        {
            next[kv.Key] = kv.Value;
        }
        foreach (long key in _removes)
        {
            next.Remove(key);
        }
        return next;
    }

    [Benchmark(Description = "ImmutableDictionary.SetItems + RemoveRange")]
    public object ImmutableDictionary_ApplyBatch() => _immutable.SetItems(_batch).RemoveRange(_removes);

    [Benchmark(Description = "FrozenDictionary rebuild")]
    public object FrozenDictionary_Rebuild()
    {
        var next = new Dictionary<long, Row>(_plain);
        foreach (var kv in _batch)
        {
            next[kv.Key] = kv.Value;
        }
        foreach (long key in _removes)
        {
            next.Remove(key);
        }
        return next.ToFrozenDictionary();
    }
}
