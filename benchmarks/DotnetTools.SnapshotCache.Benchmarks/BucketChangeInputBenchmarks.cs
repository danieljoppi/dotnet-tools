using BenchmarkDotNet.Attributes;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Issue #45: the allocation cost of the batch <i>input</i> on the incremental-refresh path — one
/// entity appended to each of N existing keys. Three ways to feed the same changes:
/// <list type="bullet">
///   <item><c>Inline_MaterializedBatch</c> — the single-entity <c>Append(key, entity)</c> overload
///   (no per-change array), collected into a <c>BucketChange[]</c>.</item>
///   <item><c>ArrayWrapped_MaterializedBatch</c> — <c>Append(key, new[] { entity })</c>, one small
///   array per change on top of the batch array (the pre-#45 shape).</item>
///   <item><c>Inline_LazyStream</c> — inline overload streamed as a lazy <c>IEnumerable</c>, so no
///   <c>BucketChange[]</c> is materialized at all (the leanest input).</item>
/// </list>
/// The table is re-seeded to the same N-key population before every invocation, so each measures
/// only the batch input plus one <c>ApplyChanges</c> over identical state. Allocation is the signal.
/// </summary>
[MemoryDiagnoser]
public class BucketChangeInputBenchmarks
{
    [Params(100_000)]
    public int N;

    private KeyValuePair<long, IReadOnlyList<string>>[] _seed = null!;
    private string[] _values = null!;
    private MultiValueSnapshotTable<long, string> _table = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = Enumerable.Range(0, N)
            .Select(i => KeyValuePair.Create((long)i, (IReadOnlyList<string>)new[] { $"s{i}a", $"s{i}b" }))
            .ToArray();
        _values = Enumerable.Range(0, N).Select(i => $"v{i}").ToArray();
    }

    // Fresh, identically-populated table before each invocation (outside the measured region).
    [IterationSetup]
    public void ReseedTable()
    {
        _table = new MultiValueSnapshotTable<long, string>(keyCapacityHint: N);
        _table.Reset(_seed);
    }

    [Benchmark(Baseline = true, Description = "Inline, materialized batch")]
    public int Inline_MaterializedBatch()
    {
        var batch = new BucketChange<long, string>[N];
        for (int i = 0; i < N; i++)
        {
            batch[i] = BucketChange.Append((long)i, _values[i]);
        }
        _table.ApplyChanges(batch);
        return _table.KeyCount;
    }

    [Benchmark(Description = "Array-wrapped, materialized batch")]
    public int ArrayWrapped_MaterializedBatch()
    {
        var batch = new BucketChange<long, string>[N];
        for (int i = 0; i < N; i++)
        {
            batch[i] = BucketChange.Append((long)i, new[] { _values[i] });
        }
        _table.ApplyChanges(batch);
        return _table.KeyCount;
    }

    [Benchmark(Description = "Inline, lazy stream (no batch array)")]
    public int Inline_LazyStream()
    {
        _table.ApplyChanges(Enumerable.Range(0, N).Select(i => BucketChange.Append((long)i, _values[i])));
        return _table.KeyCount;
    }
}
