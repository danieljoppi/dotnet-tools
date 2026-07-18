using System.Diagnostics;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Long-running stability validation: hundreds of refresh cycles with a realistic mix of updates,
/// inserts and removes, reader threads hammering lookups, and a rotating window of held old
/// snapshots (simulating in-flight reports). The claim under test: heap and LOH stay flat over
/// time — no fragmentation creep, no leak through retained snapshots.
/// </summary>
public static class SoakValidation
{
    public static void Run(int rows = 20_000_000, int cycles = 400, int batchSize = 20_000)
    {
        Console.WriteLine($"# Soak: {rows:N0} rows, {cycles} cycles of {batchSize:N0} changes " +
                          $"(70% update / 15% insert / 15% remove), 2 readers, 5 held snapshots");
        Console.WriteLine($"GC mode: {(System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation")}");

        var table = new SnapshotTable<long, long>(new SnapshotTableOptions<long> { CapacityHint = rows });
        var loadClock = Stopwatch.StartNew();
        table.ResetParallel(GenerateRows(rows));
        loadClock.Stop();
        Console.WriteLine($"Parallel load: {loadClock.Elapsed.TotalSeconds:F1} s");

        ForceFullGc();
        long heapStart = GC.GetTotalMemory(true);
        long lohStart = LohBytes();
        Console.WriteLine($"Baseline: heap {heapStart / 1_048_576.0:F0} MiB, LOH {lohStart / 1_048_576.0:F1} MiB");
        Console.WriteLine();

        using var stop = new CancellationTokenSource();
        long reads = 0;
        var readers = Enumerable.Range(0, 2).Select(r => Task.Run(() =>
        {
            var rng = new Random(9000 + r);
            long local = 0;
            while (!stop.IsCancellationRequested)
            {
                table.TryGetValue(rng.NextInt64(rows), out _);
                local++;
            }
            Interlocked.Add(ref reads, local);
        })).ToArray();

        var rng = new Random(42);
        long nextNewKey = rows;
        var removedPool = new Queue<long>();
        var heldSnapshots = new Queue<SnapshotTable<long, long>.TableSnapshot>();
        var applyTimes = new List<double>(cycles);
        int gen2Start = GC.CollectionCount(2);
        var soakClock = Stopwatch.StartNew();

        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            int updates = batchSize * 70 / 100;
            int inserts = batchSize * 15 / 100;
            int removals = batchSize - updates - inserts;

            var upserts = new List<KeyValuePair<long, long>>(updates + inserts);
            for (int i = 0; i < updates; i++)
            {
                upserts.Add(KeyValuePair.Create(rng.NextInt64(rows), (long)-cycle));
            }
            for (int i = 0; i < inserts; i++)
            {
                // Re-insert previously removed keys half the time, brand-new keys otherwise,
                // so the table size stays roughly stable instead of growing without bound.
                long key = removedPool.Count > 0 && rng.Next(2) == 0 ? removedPool.Dequeue() : nextNewKey++;
                upserts.Add(KeyValuePair.Create(key, (long)cycle));
            }
            var removes = new List<long>(removals);
            for (int i = 0; i < removals; i++)
            {
                long key = rng.NextInt64(rows);
                removes.Add(key);
                removedPool.Enqueue(key);
            }

            var sw = Stopwatch.StartNew();
            table.ApplyChanges(upserts, removes);
            sw.Stop();
            applyTimes.Add(sw.Elapsed.TotalMilliseconds);

            heldSnapshots.Enqueue(table.GetSnapshot());
            if (heldSnapshots.Count > 5)
            {
                heldSnapshots.Dequeue();
            }

            if (cycle % 100 == 0)
            {
                ForceFullGc();
                Console.WriteLine($"cycle {cycle}: heap {GC.GetTotalMemory(true) / 1_048_576.0:F0} MiB, " +
                                  $"LOH {LohBytes() / 1_048_576.0:F1} MiB, rows {table.Count:N0}, " +
                                  $"apply p50 {Median(applyTimes):F0} ms");
            }
        }
        soakClock.Stop();

        stop.Cancel();
        Task.WaitAll(readers);
        heldSnapshots.Clear();

        ForceFullGc();
        long heapEnd = GC.GetTotalMemory(true);
        long lohEnd = LohBytes();
        int gen2 = GC.CollectionCount(2) - gen2Start;

        Console.WriteLine();
        Console.WriteLine($"Soak wall time: {soakClock.Elapsed.TotalMinutes:F1} min, " +
                          $"apply p50 {Median(applyTimes):F0} ms / max {applyTimes.Max():F0} ms");
        Console.WriteLine($"Reads sustained: {reads / soakClock.Elapsed.TotalSeconds / 1_000_000:F2} M lookups/s");
        Console.WriteLine($"Gen2 collections during soak: {gen2} (excluding forced checkpoints)");
        Console.WriteLine($"Heap: {heapStart / 1_048_576.0:F0} → {heapEnd / 1_048_576.0:F0} MiB " +
                          $"(drift {(heapEnd - heapStart) / 1_048_576.0:+0;-0} MiB)");
        Console.WriteLine($"LOH:  {lohStart / 1_048_576.0:F1} → {lohEnd / 1_048_576.0:F1} MiB " +
                          $"(growth {(lohEnd - lohStart) / 1_048_576.0:F1} MiB)");

        // Table grows slightly (net new keys); allow heap drift proportional to that plus slack.
        long netNewRows = table.Count - rows;
        long allowedDrift = 64 * 1_048_576 + netNewRows * 64;
        bool lohOk = lohEnd - lohStart < 1_048_576;
        bool heapOk = heapEnd - heapStart < allowedDrift;
        Console.WriteLine();
        Console.WriteLine($"RESULT: {(lohOk && heapOk ? "PASS" : "FAIL")} " +
                          $"(LOH flat: {lohOk}, heap drift within {allowedDrift / 1_048_576} MiB: {heapOk})");
        if (!lohOk || !heapOk)
        {
            Environment.ExitCode = 1;
        }
    }

    private static IEnumerable<KeyValuePair<long, long>> GenerateRows(int count)
    {
        for (long i = 0; i < count; i++)
        {
            yield return KeyValuePair.Create(i, i * 3);
        }
    }

    private static void ForceFullGc()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static long LohBytes()
    {
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
