using System.Diagnostics;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>
/// Fast performance guardrails meant to run on every merge request (unlike the full
/// BenchmarkDotNet suite, which takes tens of minutes). They assert the *shape* of the cost —
/// allocation proportional to the batch, allocation-free reads, batch apply beating a full
/// rebuild — using generous ratios so they stay stable on noisy CI runners.
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests
{
    private const int TableSize = 1_000_000;
    private const int BatchSize = 5_000;

    private static SnapshotTable<long, long> BuildTable()
    {
        var table = new SnapshotTable<long, long>(capacityHint: TableSize);
        table.Reset(Enumerable.Range(0, TableSize).Select(i => KeyValuePair.Create((long)i, (long)i)));
        return table;
    }

    private static KeyValuePair<long, long>[] BuildBatch()
    {
        var rng = new Random(42);
        return Enumerable.Range(0, BatchSize)
            .Select(_ => (long)rng.Next(TableSize))
            .Distinct()
            .Select(k => KeyValuePair.Create(k, -k))
            .ToArray();
    }

    [Fact]
    public void Reads_AreAllocationFree()
    {
        var table = BuildTable();
        var snapshot = table.GetSnapshot();
        // Warm up so any one-time lazy allocation doesn't count.
        table.TryGetValue(1, out _);
        snapshot.TryGetValue(1, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (long k = 0; k < 100_000; k++)
        {
            table.TryGetValue(k % TableSize, out _);
            snapshot.TryGetValue((k * 31) % TableSize, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"200k lookups allocated {allocated} bytes; expected 0.");
    }

    [Fact]
    public void BatchUpdate_AllocationIsProportionalToBatch_NotTableSize()
    {
        var table = BuildTable();
        var batch = BuildBatch();
        table.ApplyChanges(batch); // warm-up: JIT + first-touch

        long before = GC.GetAllocatedBytesForCurrentThread();
        table.ApplyChanges(batch);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A full copy of 1M KeyValuePair<long,long> rows alone is ~16 MB, plus a rebuilt 1M-entry
        // index would be ~28 MB more. Copy-on-write of ~5k touched chunks/shards must stay far
        // below that; 20 MB is a very loose ceiling that still proves O(batch) behaviour.
        Assert.True(allocated < 20_000_000,
            $"5k-row batch on a 1M-row table allocated {allocated / 1_000_000.0:F1} MB; expected O(batch), < 20 MB.");
    }

    [Fact]
    public void BatchUpdate_DoesNotAllocateOnLargeObjectHeap()
    {
        var table = BuildTable();
        var batch = BuildBatch();
        table.ApplyChanges(batch); // warm-up

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetGCMemoryInfo(GCKind.Any);
        long lohBefore = GetLohAllocated();

        for (int i = 0; i < 10; i++)
        {
            table.ApplyChanges(batch);
        }

        long lohGrowth = GetLohAllocated() - lohBefore;
        // Ten refresh cycles must not have pushed anything onto the LOH. Allow a small slack for
        // runtime-internal LOH activity unrelated to the table.
        Assert.True(lohGrowth < 1_000_000,
            $"10 batch refreshes grew LOH allocations by {lohGrowth} bytes; expected none.");

        static long GetLohAllocated()
        {
            // Generation index 3 = LOH in GetGCMemoryInfo generation stats after a collection;
            // use total committed LOH bytes as a monotonic-enough proxy.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            return info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
        }
    }

    [Fact]
    public void BatchUpdate_IsFasterThanFullRebuild()
    {
        var table = BuildTable();
        var batch = BuildBatch();
        var plain = new Dictionary<long, long>(
            Enumerable.Range(0, TableSize).Select(i => KeyValuePair.Create((long)i, (long)i)));

        // Warm-up both paths.
        table.ApplyChanges(batch);
        RebuildAndSwap(plain, batch);

        TimeSpan applyTime = Median(() => table.ApplyChanges(batch));
        TimeSpan rebuildTime = Median(() => RebuildAndSwap(plain, batch));

        // The whole reason this class exists: applying a 5k batch must beat copying 1M entries.
        // Require only 2x to keep the test robust on slow/noisy CI machines (locally it's >10x).
        Assert.True(applyTime * 2 < rebuildTime,
            $"ApplyChanges median {applyTime.TotalMilliseconds:F2} ms vs full rebuild " +
            $"{rebuildTime.TotalMilliseconds:F2} ms; expected at least 2x faster.");

        static void RebuildAndSwap(Dictionary<long, long> source, KeyValuePair<long, long>[] changes)
        {
            var next = new Dictionary<long, long>(source);
            foreach (var (k, v) in changes)
            {
                next[k] = v;
            }
            GC.KeepAlive(next);
        }

        static TimeSpan Median(Action action)
        {
            var samples = new TimeSpan[5];
            for (int i = 0; i < samples.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                action();
                samples[i] = sw.Elapsed;
            }
            Array.Sort(samples);
            return samples[samples.Length / 2];
        }
    }

    [Fact]
    public void SnapshotRetention_CostsOnlyTheDelta()
    {
        // Holding an old snapshot across a batch must not double memory: the two versions share
        // all untouched chunks. A *clustered* batch (keys in one region of the table) must copy
        // only the chunks it touches. (A uniformly random 5k batch over ~245 chunks touches nearly
        // every chunk, so sharing can only be observed with a clustered one.)
        var table = BuildTable();
        var before = table.GetSnapshot();
        table.ApplyChanges(Enumerable.Range(0, BatchSize).Select(i => KeyValuePair.Create((long)i, -1L)));
        var after = table.GetSnapshot();

        int shared = 0, total = 0;
        var beforeRows = before.Rows;
        var afterRows = after.Rows;
        // Compare row storage identity chunk by chunk through the internal accessor.
        foreach (var (a, b) in EnumerateChunkPairs(beforeRows, afterRows))
        {
            total++;
            if (ReferenceEquals(a, b))
            {
                shared++;
            }
        }
        Assert.True(total > 100, $"expected a chunked spine, saw {total} chunks");
        Assert.True(shared > total * 0.90,
            $"only {shared}/{total} chunks shared after a 5k batch; copy-on-write is broken.");

        static IEnumerable<(object?, object?)> EnumerateChunkPairs(
            ChunkedImmutableList<KeyValuePair<long, long>> x,
            ChunkedImmutableList<KeyValuePair<long, long>> y)
        {
            var fx = typeof(ChunkedImmutableList<KeyValuePair<long, long>>)
                .GetField("_chunks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var cx = (KeyValuePair<long, long>[][])fx.GetValue(x)!;
            var cy = (KeyValuePair<long, long>[][])fx.GetValue(y)!;
            for (int i = 0; i < Math.Min(cx.Length, cy.Length); i++)
            {
                yield return (cx[i], cy[i]);
            }
        }
    }
}
