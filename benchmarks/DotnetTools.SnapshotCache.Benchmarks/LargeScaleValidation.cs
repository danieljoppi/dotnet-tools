using System.Diagnostics;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// End-to-end validation of the target workload, run as a plain console harness (BenchmarkDotNet
/// process isolation would double the multi-GB working set): a 100,000,000-row table refreshed
/// with 20,000 changes per cycle while reader threads hammer point lookups. Reports per-cycle
/// latency, allocation, GC collections by generation, and LOH size — the claim under test is
/// "O(batch) refresh cost and zero LOH growth at 100M rows".
/// </summary>
public static class LargeScaleValidation
{
    public static void Run(int rows = 100_000_000, int batchSize = 20_000, int cycles = 10)
    {
        Console.WriteLine($"# Large-scale validation: {rows:N0} rows, {batchSize:N0} changes/cycle, {cycles} cycles");
        Console.WriteLine($"GC mode: {(System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation")}, " +
                          $"process: {Environment.ProcessorCount} cores");
        Console.WriteLine();

        var table = new SnapshotTable<long, long>(new SnapshotTableOptions<long> { CapacityHint = rows });

        var sw = Stopwatch.StartNew();
        table.Reset(GenerateRows(rows));
        sw.Stop();
        ForceFullGc();
        var afterLoad = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        long lohAfterLoad = LohSizeBytes(afterLoad);
        Console.WriteLine($"Initial load: {sw.Elapsed.TotalSeconds:F1} s " +
                          $"({rows / sw.Elapsed.TotalSeconds / 1_000_000:F1} M rows/s)");
        Console.WriteLine($"Heap after load: {afterLoad.HeapSizeBytes / 1_073_741_824.0:F2} GiB " +
                          $"(LOH: {lohAfterLoad / 1_048_576.0:F1} MiB)");
        Console.WriteLine();

        // Readers run through every refresh cycle, verifying reads stay wait-free and correct.
        using var stopReaders = new CancellationTokenSource();
        long totalReads = 0;
        var readers = Enumerable.Range(0, 2).Select(r => Task.Run(() =>
        {
            var rng = new Random(1000 + r);
            long reads = 0;
            while (!stopReaders.IsCancellationRequested)
            {
                long key = rng.NextInt64(rows);
                if (!table.TryGetValue(key, out _))
                {
                    throw new InvalidOperationException($"Key {key} missing during refresh — torn read!");
                }
                reads++;
            }
            Interlocked.Add(ref totalReads, reads);
        })).ToArray();

        var rng = new Random(42);
        long nextNewKey = rows;
        int gen0Before = GC.CollectionCount(0), gen1Before = GC.CollectionCount(1), gen2Before = GC.CollectionCount(2);
        var cycleTimes = new List<double>();
        var readClock = Stopwatch.StartNew();

        Console.WriteLine("| Cycle | Apply time | Allocated (writer) | Table rows |");
        Console.WriteLine("|---|---|---|---|");
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            // The 30-second refresh: 80% updates of existing rows, 20% inserts of new customers.
            int updates = batchSize * 8 / 10;
            var batch = new KeyValuePair<long, long>[batchSize];
            for (int i = 0; i < updates; i++)
            {
                batch[i] = KeyValuePair.Create(rng.NextInt64(rows), (long)-cycle);
            }
            for (int i = updates; i < batchSize; i++)
            {
                batch[i] = KeyValuePair.Create(nextNewKey++, (long)cycle);
            }

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            table.ApplyChanges(batch);
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            cycleTimes.Add(sw.Elapsed.TotalMilliseconds);
            Console.WriteLine($"| {cycle} | {sw.Elapsed.TotalMilliseconds:F1} ms | {allocated / 1_048_576.0:F1} MiB | {table.Count:N0} |");
        }
        readClock.Stop();

        stopReaders.Cancel();
        Task.WaitAll(readers);

        int gen0 = GC.CollectionCount(0) - gen0Before;
        int gen1 = GC.CollectionCount(1) - gen1Before;
        int gen2 = GC.CollectionCount(2) - gen2Before;

        ForceFullGc();
        var final = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        long lohFinal = LohSizeBytes(final);

        Console.WriteLine();
        Console.WriteLine($"Apply time: median {Median(cycleTimes):F1} ms, max {cycleTimes.Max():F1} ms " +
                          $"(budget: 30,000 ms per cycle)");
        Console.WriteLine($"GC during {cycles} cycles: Gen0={gen0} Gen1={gen1} Gen2={gen2}");
        Console.WriteLine($"Concurrent read throughput during refreshes: " +
                          $"{totalReads / readClock.Elapsed.TotalSeconds / 1_000_000:F2} M lookups/s across 2 readers");
        Console.WriteLine($"LOH size: after load {lohAfterLoad / 1_048_576.0:F1} MiB → final {lohFinal / 1_048_576.0:F1} MiB " +
                          $"(growth {(lohFinal - lohAfterLoad) / 1_048_576.0:F1} MiB)");
        Console.WriteLine($"Final heap: {final.HeapSizeBytes / 1_073_741_824.0:F2} GiB, table rows: {table.Count:N0}");

        bool lohOk = lohFinal - lohAfterLoad < 1_048_576;
        bool timeOk = cycleTimes.Max() < 30_000;
        Console.WriteLine();
        Console.WriteLine($"RESULT: {(lohOk && timeOk ? "PASS" : "FAIL")} " +
                          $"(LOH growth {(lohOk ? "none" : "DETECTED")}, refresh fits budget: {timeOk})");
        if (!lohOk || !timeOk)
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
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static long LohSizeBytes(GCMemoryInfo info) =>
        info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
