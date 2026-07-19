using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Investigation: how far can an <see cref="ImmutableArray{T}"/>-based cache be improved with
/// known techniques, and what does each technique give up? Four variants apply the same batch to
/// a table of N rows, per cycle:
///
/// <list type="number">
///   <item><b>Naive rebuild</b> — <c>ToBuilder()</c> + <c>ToImmutable()</c>: what straightforward
///   code does today (two full copies, fresh LOH allocation every cycle).</item>
///   <item><b>Uninitialized + wrap</b> — <c>GC.AllocateUninitializedArray</c>, one
///   <c>CopyTo</c>, then <c>ImmutableCollectionsMarshal.AsImmutableArray</c>: one copy, one LOH
///   allocation, skips page zeroing.</item>
///   <item><b>Pooled double-buffer</b> — two persistent arrays alternate; copy into the standby,
///   apply the batch, wrap, swap: <i>zero</i> steady-state allocation — but the array behind a
///   published snapshot is silently overwritten one cycle later, so immutability is a promise
///   the caller must keep (no snapshot may outlive a single refresh), and inserts that grow N
///   force a reallocation anyway.</item>
///   <item><b>SnapshotTable</b> reference — the chunked copy-on-write answer.</item>
/// </list>
///
/// All ImmutableArray variants also carry two costs no allocation trick removes: the O(N) copy
/// time per cycle regardless of batch size, and the lack of a keyed index (a real cache pairs
/// the array with a Dictionary that is itself LOH-resident and must be rebuilt on any reorder).
/// </summary>
public static class ImmutableArrayStudy
{
    public static void Run(int rows = 10_000_000, int batchSize = 20_000, int cycles = 15)
    {
        Console.WriteLine($"# ImmutableArray improvement study: {rows:N0} rows, " +
                          $"{batchSize:N0}-change batch, {cycles} cycles per variant");
        Console.WriteLine();
        Console.WriteLine("| Variant | Apply p50 | Alloc/cycle¹ | Gen2 collections | Caveat |");
        Console.WriteLine("|---|---:|---:|---:|---|");

        var rng = new Random(42);
        var batchKeys = Enumerable.Range(0, batchSize).Select(_ => rng.Next(rows)).Distinct().ToArray();

        RunVariant("Naive ToBuilder/ToImmutable", rows, cycles, batchKeys, "fresh LOH array every cycle",
            source =>
            {
                var current = ImmutableArray.CreateRange(source);
                return () =>
                {
                    var builder = current.ToBuilder();
                    foreach (int k in batchKeys)
                    {
                        builder[k] = new KeyValuePair<long, long>(k, -1);
                    }
                    current = builder.ToImmutable();
                    return current;
                };
            });

        RunVariant("Uninitialized alloc + AsImmutableArray", rows, cycles, batchKeys, "still 1 LOH alloc/cycle",
            source =>
            {
                var current = ImmutableArray.CreateRange(source);
                return () =>
                {
                    var next = GC.AllocateUninitializedArray<KeyValuePair<long, long>>(rows);
                    current.CopyTo(next);
                    foreach (int k in batchKeys)
                    {
                        next[k] = new KeyValuePair<long, long>(k, -1);
                    }
                    current = ImmutableCollectionsMarshal.AsImmutableArray(next);
                    return current;
                };
            });

        RunVariant("Builder + MoveToImmutable (safe)", rows, cycles, batchKeys,
            "1 LOH alloc/cycle; the safe way to avoid the second copy",
            source =>
            {
                var current = ImmutableArray.CreateRange(source);
                return () =>
                {
                    var builder = ImmutableArray.CreateBuilder<KeyValuePair<long, long>>(rows);
                    builder.AddRange(current);
                    foreach (int k in batchKeys)
                    {
                        builder[k] = new KeyValuePair<long, long>(k, -1);
                    }
                    current = builder.MoveToImmutable(); // transfers ownership, no copy
                    return current;
                };
            });

        RunVariant("Extract (AsArray) + mutate in place", rows, cycles, batchKeys,
            "NO atomicity: readers see half-applied batches mid-write",
            source =>
            {
                var current = ImmutableArray.CreateRange(source);
                var raw = ImmutableCollectionsMarshal.AsArray(current)!;
                return () =>
                {
                    foreach (int k in batchKeys)
                    {
                        raw[k] = new KeyValuePair<long, long>(k, -1);
                    }
                    return current;
                };
            });

        RunVariant("Pooled double-buffer + wrap", rows, cycles, batchKeys,
            "UNSAFE past 1 cycle: held snapshots get mutated; growth reallocates",
            source =>
            {
                var bufferA = source.ToArray();
                var bufferB = new KeyValuePair<long, long>[rows];
                var current = ImmutableCollectionsMarshal.AsImmutableArray(bufferA);
                bool aIsCurrent = true;
                return () =>
                {
                    var standby = aIsCurrent ? bufferB : bufferA;
                    current.CopyTo(standby);
                    foreach (int k in batchKeys)
                    {
                        standby[k] = new KeyValuePair<long, long>(k, -1);
                    }
                    current = ImmutableCollectionsMarshal.AsImmutableArray(standby);
                    aIsCurrent = !aIsCurrent;
                    return current;
                };
            });

        RunVariant("SnapshotTable.ApplyChanges (reference)", rows, cycles, batchKeys, "none — designed for this",
            source =>
            {
                var table = new SnapshotTable<long, long>(capacityHint: rows);
                table.ResetParallel(source);
                var batch = batchKeys.Select(k => KeyValuePair.Create((long)k, -1L)).ToArray();
                return () =>
                {
                    table.ApplyChanges(batch);
                    return table;
                };
            });
    }

    private static void RunVariant(
        string name, int rows, int cycles, int[] batchKeys, string caveat,
        Func<IEnumerable<KeyValuePair<long, long>>, Func<object>> setup)
    {
        var source = Enumerable.Range(0, rows).Select(i => KeyValuePair.Create((long)i, (long)i));
        var applyOnce = setup(source);
        applyOnce(); // warm-up

        ForceFullGc();
        long lohBefore = LohBytes();
        int gen2Before = GC.CollectionCount(2);
        var times = new List<double>(cycles);
        long allocTotal = 0;
        object? keepAlive = null;
        for (int i = 0; i < cycles; i++)
        {
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            keepAlive = applyOnce();
            sw.Stop();
            allocTotal += GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        int gen2 = GC.CollectionCount(2) - gen2Before;
        ForceFullGc();
        GC.KeepAlive(keepAlive);

        times.Sort();
        double p50 = times[times.Count / 2];
        Console.WriteLine($"| {name} | {p50:F0} ms | {allocTotal / times.Count / 1_048_576.0:F1} MiB | " +
                          $"{gen2} | {caveat} |");
        _ = lohBefore;
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
}
