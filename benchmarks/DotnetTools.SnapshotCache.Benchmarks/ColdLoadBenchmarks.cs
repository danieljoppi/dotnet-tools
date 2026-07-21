using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Cold-load cost of <see cref="MultiValueSnapshotTable{TKey, TEntity}"/> (issues #42/#43): the
/// three ways to populate an empty table, contrasted so the O(N²) footgun is visible in the
/// allocation column.
/// <list type="bullet">
///   <item><c>Reset</c> — the one-shot path: builds every shard once, O(total entities).</item>
///   <item><c>BatchedApplyChanges</c> — the whole load handed to <c>ApplyChanges</c> as one
///   batch; one snapshot transition, same O(N) band as <c>Reset</c>.</item>
///   <item><c>PerKeyApplyChanges</c> — <b>the footgun</b>: one <c>ApplyChanges</c> call per key.
///   Every call clones the shard directory and copy-on-writes a whole shard dictionary, so this is
///   O(N²) in shard occupancy — the pattern that kept a production process unhealthy for 15+
///   minutes. Capped at 100k keys (1M would run for minutes, which is the point).</item>
/// </list>
/// Each method builds a fresh table per invocation, so the class pins <see cref="RunStrategy.ColdStart"/>
/// (InvocationCount=1, no warmup) — the same one-shot discipline <c>SecondaryIndexBenchmarks</c>
/// uses. Timings on a shared runner are noisy (ADR-0005); the <b>Allocated</b> column and the ratio
/// to the <c>Reset</c> baseline are the stable signals. LOH occupancy is not visible here — the
/// <c>Category=Performance</c> guardrail <c>ColdLoad_PerKeyApplyChanges_AllocatesOrdersOfMagnitudeMoreThanReset</c>
/// is the CI gate that actually fails on the footgun.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.ColdStart, iterationCount: 5)]
public class ColdLoadBenchmarks
{
    private const int ValuesPerKey = 4;

    // Value buckets per key count, built once so the measured region is only the table population.
    private readonly Dictionary<int, long[][]> _buckets = new();

    [GlobalSetup]
    public void Setup()
    {
        foreach (int keys in new[] { 10_000, 100_000, 1_000_000 })
        {
            var buckets = new long[keys][];
            for (int k = 0; k < keys; k++)
            {
                var values = new long[ValuesPerKey];
                for (int v = 0; v < ValuesPerKey; v++)
                {
                    values[v] = ((long)k * ValuesPerKey) + v;
                }
                buckets[k] = values;
            }
            _buckets[keys] = buckets;
        }
    }

    [Benchmark(Baseline = true, Description = "Reset (one-shot)")]
    [Arguments(10_000)]
    [Arguments(100_000)]
    [Arguments(1_000_000)]
    public MultiValueSnapshotTable<long, long> Reset(int keys)
    {
        var table = new MultiValueSnapshotTable<long, long>(keyCapacityHint: keys);
        table.Reset(EnumerateBuckets(_buckets[keys]));
        return table;
    }

    [Benchmark(Description = "Batched ApplyChanges (one call)")]
    [Arguments(10_000)]
    [Arguments(100_000)]
    [Arguments(1_000_000)]
    public MultiValueSnapshotTable<long, long> BatchedApplyChanges(int keys)
    {
        var buckets = _buckets[keys];
        var changes = new BucketChange<long, long>[keys];
        for (int k = 0; k < keys; k++)
        {
            changes[k] = BucketChange.Append((long)k, buckets[k]);
        }
        var table = new MultiValueSnapshotTable<long, long>(keyCapacityHint: keys);
        table.ApplyChanges(changes);
        return table;
    }

    // The footgun. 1M is intentionally omitted from [Arguments]: at O(N²) it runs for minutes,
    // which is exactly the "never Ready" production symptom this benchmark exists to make visible.
    [Benchmark(Description = "Per-key ApplyChanges (FOOTGUN, O(N^2))")]
    [Arguments(10_000)]
    [Arguments(100_000)]
    public MultiValueSnapshotTable<long, long> PerKeyApplyChanges(int keys)
    {
        var buckets = _buckets[keys];
        var table = new MultiValueSnapshotTable<long, long>(keyCapacityHint: keys);
        for (int k = 0; k < keys; k++)
        {
            table.ApplyChanges([BucketChange.Append((long)k, buckets[k])]);
        }
        return table;
    }

    private static IEnumerable<KeyValuePair<long, IReadOnlyList<long>>> EnumerateBuckets(long[][] buckets)
    {
        for (int k = 0; k < buckets.Length; k++)
        {
            yield return new KeyValuePair<long, IReadOnlyList<long>>(k, buckets[k]);
        }
    }
}
