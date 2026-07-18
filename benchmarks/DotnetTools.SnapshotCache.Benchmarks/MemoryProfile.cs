using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Measures the steady-state resident memory of one structure holding N keyed rows
/// (<c>long → long</c>), split into total managed heap and the Large Object Heap portion.
/// Run one structure per process (the driver script does this) so measurements can't
/// contaminate each other: build → forced full compacting GC → delta against baseline.
/// Output is one CSV line: structure,rows,heapBytes,lohBytes.
/// </summary>
public static class MemoryProfile
{
    public static void Run(string structure, int rows)
    {
        ForceGc();
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        long lohBefore = LohBytes();

        object handle = Build(structure, rows);

        ForceGc();
        long heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        long lohAfter = LohBytes();

        Console.WriteLine($"{structure},{rows},{heapAfter - heapBefore},{lohAfter - lohBefore}");
        GC.KeepAlive(handle);
    }

    private static object Build(string structure, int rows)
    {
        var source = Enumerable.Range(0, rows).Select(i => KeyValuePair.Create((long)i, (long)i * 3));
        switch (structure)
        {
            case "SnapshotTable":
                var table = new SnapshotTable<long, long>(capacityHint: rows);
                table.Reset(source);
                return table;
            case "Dictionary":
                return new Dictionary<long, long>(source);
            case "FrozenDictionary":
                return source.ToFrozenDictionary();
            case "ConcurrentDictionary":
                return new ConcurrentDictionary<long, long>(source);
            case "ImmutableDictionary":
                return ImmutableDictionary.CreateRange(source);
            case "ImmutableList":
                return ImmutableList.CreateRange(source);
            case "ImmutableArray":
                return ImmutableArray.CreateRange(source);
            default:
                throw new ArgumentException($"Unknown structure '{structure}'.", nameof(structure));
        }
    }

    private static void ForceGc()
    {
        // LOH is not compacted by a normal forced collection; request it explicitly so the
        // reported LOH size is live bytes, not segment size including free space.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static long LohBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
    }
}
