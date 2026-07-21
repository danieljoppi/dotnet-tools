using BenchmarkDotNet.Attributes;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Cost of registering a secondary index. <c>CreateIndex</c> on an already-populated table
/// (issue #20) backfills the index with a one-time O(rows) scan of the current snapshot; this
/// measures that scan against the same index built eagerly before load (where the per-row index
/// work is folded into <c>Reset</c> instead). The table is rebuilt in <c>[IterationSetup]</c> so
/// each invocation indexes a fresh table exactly once — timing runs at InvocationCount=1 (noisy on
/// shared CI, as documented), while the allocation column is the stable signal for the index's
/// one-time footprint.
/// </summary>
[MemoryDiagnoser]
public class SecondaryIndexBenchmarks
{
    public sealed record Row(long Id, string Region, int Status);

    private static readonly string[] Regions = ["BR", "US", "DE", "JP", "IN", "UK", "FR", "AU"];

    [Params(1_000_000)]
    public int N;

    private KeyValuePair<long, Row>[] _rows = null!;
    private SnapshotTable<long, Row> _loaded = null!;

    [GlobalSetup]
    public void Setup() =>
        _rows = Enumerable.Range(0, N)
            .Select(i => KeyValuePair.Create((long)i, new Row(i, Regions[i % Regions.Length], i % 5)))
            .ToArray();

    // Fresh, populated table per invocation (outside the measured region).
    [IterationSetup(Target = nameof(CreateIndex_BackfillAfterLoad))]
    public void RebuildLoaded()
    {
        _loaded = new SnapshotTable<long, Row>(capacityHint: N);
        _loaded.Reset(_rows);
    }

    /// <summary>Issue #20: register the index on a table that already holds N rows — the backfill scan.</summary>
    [Benchmark(Description = "CreateIndex backfill on a loaded table")]
    public object CreateIndex_BackfillAfterLoad() =>
        _loaded.CreateIndex((_, r) => r.Region);

    /// <summary>Reference: the index registered before load, so the per-row index work happens
    /// inside Reset. Measures load+index end-to-end (not directly comparable to the backfill scan,
    /// but bounds the total cost of the eager path).</summary>
    [Benchmark(Description = "Reset with the index registered up front")]
    public object Reset_WithEagerIndex()
    {
        var table = new SnapshotTable<long, Row>(capacityHint: N);
        table.CreateIndex((_, r) => r.Region);
        table.Reset(_rows);
        return table;
    }
}
