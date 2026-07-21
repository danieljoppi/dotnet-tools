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
        // index would be ~28 MB more. Copy-on-write of a uniformly random 5k batch over ~245 chunks
        // touches nearly every chunk and measures ~15.3 MB. Gate as a *band* around that baseline:
        // the upper bound catches a regression toward O(N); the lower bound catches an accidental
        // under-copy (a correctness bug that skips touched chunks would allocate far less). Both
        // bounds are deterministic — allocation, unlike wall time, does not drift on CI runners.
        Assert.InRange(allocated, 8_000_000, 18_000_000);
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
    public void BatchUpdate_DoesLessWorkThanFullRebuild()
    {
        // Guard the O(batch)-vs-O(N) property with ALLOCATED BYTES, which is deterministic, rather
        // than a wall-clock ratio: shared CI runners vary so much between assignments (the same
        // code measured 0.8x-5x across runs) that any timing threshold is either flaky or
        // meaningless. Wall-clock comparisons live in the benchmark job, which reports without
        // asserting. A very loose absolute time cap stays as a pathological-regression tripwire.
        const int size = 8 * TableSize;
        var table = new SnapshotTable<long, long>(capacityHint: size);
        table.Reset(Enumerable.Range(0, size).Select(i => KeyValuePair.Create((long)i, (long)i)));
        var rng = new Random(42);
        var batch = Enumerable.Range(0, BatchSize)
            .Select(_ => (long)rng.Next(size))
            .Distinct()
            .Select(k => KeyValuePair.Create(k, -k))
            .ToArray();
        var plain = new Dictionary<long, long>(
            Enumerable.Range(0, size).Select(i => KeyValuePair.Create((long)i, (long)i)));

        // Warm-up both paths.
        table.ApplyChanges(batch);
        RebuildAndSwap(plain, batch);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        table.ApplyChanges(batch);
        sw.Stop();
        long applyBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        RebuildAndSwap(plain, batch);
        long rebuildBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(applyBytes * 4 < rebuildBytes,
            $"ApplyChanges allocated {applyBytes / 1_048_576.0:F1} MiB vs full rebuild " +
            $"{rebuildBytes / 1_048_576.0:F1} MiB; expected at least 4x less work.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"ApplyChanges took {sw.Elapsed.TotalMilliseconds:F0} ms for a 5k batch; pathological.");

        static void RebuildAndSwap(Dictionary<long, long> source, KeyValuePair<long, long>[] changes)
        {
            var next = new Dictionary<long, long>(source);
            foreach (var (k, v) in changes)
            {
                next[k] = v;
            }
            GC.KeepAlive(next);
        }
    }

    // --- Reference-type rows: the guardrails above use long → long, but real caches hold reference
    // payloads the GC must trace, which is a different allocation and LOH profile. These repeat the
    // two load-bearing shape checks on a mid-width record row. ---

    private sealed record Row(long Id, string Name, decimal Balance, DateTime UpdatedAt);

    private static SnapshotTable<long, Row> BuildReferenceTable()
    {
        var table = new SnapshotTable<long, Row>(capacityHint: TableSize);
        table.Reset(Enumerable.Range(0, TableSize)
            .Select(i => KeyValuePair.Create((long)i, new Row(i, "row", i * 1.5m, DateTime.UnixEpoch))));
        return table;
    }

    [Fact]
    public void Reads_ReferenceRows_AreAllocationFree()
    {
        var table = BuildReferenceTable();
        var snapshot = table.GetSnapshot();
        table.TryGetValue(1, out _); // warm-up
        snapshot.TryGetValue(1, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (long k = 0; k < 100_000; k++)
        {
            table.TryGetValue(k % TableSize, out _);
            snapshot.TryGetValue((k * 31) % TableSize, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"200k reference-row lookups allocated {allocated} bytes; expected 0.");
    }

    [Fact]
    public void BatchUpdate_ReferenceRows_DoNotAllocateOnLargeObjectHeap()
    {
        var table = BuildReferenceTable();
        var rng = new Random(42);
        var batch = Enumerable.Range(0, BatchSize)
            .Select(_ => (long)rng.Next(TableSize)).Distinct()
            .Select(k => KeyValuePair.Create(k, new Row(k, "changed", -k, DateTime.UnixEpoch)))
            .ToArray();
        table.ApplyChanges(batch); // warm-up

        long lohBefore = ForcedLohBytes();
        for (int i = 0; i < 10; i++)
        {
            table.ApplyChanges(batch);
        }
        long lohGrowth = ForcedLohBytes() - lohBefore;

        Assert.True(lohGrowth < 1_000_000,
            $"10 reference-row batches grew LOH by {lohGrowth} bytes; expected none.");
    }

    // Forced compacting-free full collection, then LOH segment size — the same monotonic proxy the
    // long→long guardrail uses. Reliable only when tests are serialized (see TestParallelization.cs).
    private static long ForcedLohBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
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
            var bx = x.UnsafeBlocks;
            var by = y.UnsafeBlocks;
            for (int b = 0; b < Math.Min(bx.Length, by.Length); b++)
            {
                for (int s = 0; s < Math.Min(bx[b].Length, by[b].Length); s++)
                {
                    if (bx[b][s] is not null || by[b][s] is not null)
                    {
                        yield return (bx[b][s], by[b][s]);
                    }
                }
            }
        }
    }

    // --- MultiValueSnapshotTable cold-load footgun (issues #42/#43). Loading a whole table with one
    // ApplyChanges call per key is O(N^2) in shard occupancy: every call clones the shard directory
    // and copy-on-writes a whole shard dictionary. In production this flooded the LOH and kept the
    // process unhealthy for 15+ minutes. The correct cold-load path is Reset (or one batched
    // ApplyChanges). This is the guardrail that must fail on the per-key loop and pass on the batch;
    // it asserts on ALLOCATED BYTES (deterministic on CI), not wall time. ---

    private const int ColdLoadKeys = 20_000;

    private static IEnumerable<KeyValuePair<long, IReadOnlyList<long>>> ColdLoadBuckets() =>
        Enumerable.Range(0, ColdLoadKeys)
            .Select(i => new KeyValuePair<long, IReadOnlyList<long>>(i, new long[] { i }));

    private static long MeasureAllocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void ColdLoadWithReset()
    {
        var table = new MultiValueSnapshotTable<long, long>(keyCapacityHint: ColdLoadKeys);
        table.Reset(ColdLoadBuckets());
        GC.KeepAlive(table);
    }

    private static void ColdLoadPerKeyApplyChanges(int keys)
    {
        var table = new MultiValueSnapshotTable<long, long>(keyCapacityHint: keys);
        for (long k = 0; k < keys; k++)
        {
            table.ApplyChanges([BucketChange.Append(k, k)]);
        }
        GC.KeepAlive(table);
    }

    [Fact]
    public void ColdLoad_PerKeyApplyChanges_AllocatesOrdersOfMagnitudeMoreThanReset()
    {
        // Warm up the JIT on both paths (throwaway tables) so first-call costs aren't attributed.
        ColdLoadWithReset();
        ColdLoadPerKeyApplyChanges(1_000);

        long resetBytes = MeasureAllocated(ColdLoadWithReset);
        long perKeyBytes = MeasureAllocated(() => ColdLoadPerKeyApplyChanges(ColdLoadKeys));

        // The correct path is O(total entities): a shard directory, one entry per key, and a
        // single-element bucket each. For 20k single-entity keys that is a few MB — assert it stays
        // O(N), which also catches a regression toward O(N^2) on the recommended path itself.
        Assert.True(resetBytes < 12_000_000,
            $"Reset cold-load of {ColdLoadKeys} keys allocated {resetBytes / 1_048_576.0:F1} MiB; expected O(N).");

        // The per-key loop re-clones a shard dictionary on every call. It must dwarf Reset by an
        // order of magnitude; 20x is a conservative floor (measured ~30-40x) that stays stable on
        // noisy runners. If this ever fails because the two are close, the O(N^2) copy-on-write was
        // fixed upstream — revisit the API guidance and this guardrail together, don't just relax it.
        Assert.True(perKeyBytes > resetBytes * 20,
            $"Per-key cold-load allocated {perKeyBytes / 1_048_576.0:F1} MiB vs Reset " +
            $"{resetBytes / 1_048_576.0:F1} MiB; expected the footgun to allocate >20x more.");
    }

    [Fact]
    public void ColdLoad_BatchedApplyChanges_IsCheapLikeReset()
    {
        // The other correct cold-load path: hand the whole load to ApplyChanges as ONE batch. It
        // must land in the same O(N) band as Reset, nowhere near the per-key loop.
        var batch = Enumerable.Range(0, ColdLoadKeys)
            .Select(i => BucketChange.Append((long)i, (long)i))
            .ToArray();

        // Warm-up.
        new MultiValueSnapshotTable<long, long>(keyCapacityHint: ColdLoadKeys).ApplyChanges(batch);

        long batchedBytes = MeasureAllocated(() =>
        {
            var table = new MultiValueSnapshotTable<long, long>(keyCapacityHint: ColdLoadKeys);
            table.ApplyChanges(batch);
            GC.KeepAlive(table);
        });

        Assert.True(batchedBytes < 12_000_000,
            $"Batched cold-load of {ColdLoadKeys} keys allocated {batchedBytes / 1_048_576.0:F1} MiB; " +
            "one batch must stay O(N), not O(N^2).");
    }
}
