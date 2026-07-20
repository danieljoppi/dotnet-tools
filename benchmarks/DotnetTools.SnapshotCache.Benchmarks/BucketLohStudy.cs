using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetTools.SnapshotCache;
using Entity = DotnetTools.SnapshotCache.Benchmarks.BucketWorkload.Entity;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// The LOH side of the shared-key bucket workloads (B and C), run as a plain console harness
/// because BenchmarkDotNet's memory diagnoser reports allocation totals and GC counts but not
/// Large Object Heap occupancy. For each bucket representation it builds the population, then
/// applies warm batches (append 1–50 / replace ~1% per touched key, hot keys touched most) and
/// reports, per approach:
/// <list type="bullet">
///   <item>LOH size after build, after the batch cycles (uncompacted — the real steady-state
///   footprint), and after a forced Gen2 + LOH compaction (live large objects only);</item>
///   <item>batch wall time (median/max), allocated bytes per batch, Gen0/1/2 counts;</item>
///   <item>the forced Gen2-with-compaction pause after the cycles;</item>
///   <item>retained heap cost of holding the pre-cycle snapshot across all batches vs dropping
///   it (structural sharing makes this cheap for chunked/table, O(touched buckets) for arrays).</item>
/// </list>
/// Approaches run sequentially in one process; every phase is measured as a delta from a
/// baseline taken after a full compacting GC with the previous approach's stores released.
/// Usage: <c>--bucket-loh [entities] [buckets] [uniform|zipf|refresh] [cycles]</c>
/// </summary>
public static class BucketLohStudy
{
    private static readonly string[] Approaches =
        ["ImmArray_AddRange", "List_Then_PublishArray", "ChunkedList_Builder", "SnapshotTable_Rekeyed", "MultiValue_Table"];

    public static void Run(int entities, int buckets, string skew, int cycles)
    {
        var profile = skew.ToLowerInvariant() switch
        {
            "uniform" => BucketWorkload.SizeProfile.Uniform,
            "zipf" => BucketWorkload.SizeProfile.Zipf,
            "refresh" => BucketWorkload.SizeProfile.HeavyTailRefresh,
            _ => throw new ArgumentException($"Unknown skew '{skew}' (uniform|zipf|refresh)."),
        };
        bool weighted = profile != BucketWorkload.SizeProfile.Uniform;
        int touchCount = profile == BucketWorkload.SizeProfile.HeavyTailRefresh
            ? Math.Min(800, buckets)
            : Math.Max(1, buckets / 100);

        var sizes = BucketWorkload.BuildSizes(profile, buckets, entities);
        int lohBuckets = sizes.Count(s => (long)s * IntPtr.Size >= 85_000);
        Console.WriteLine($"# Bucket LOH study: {entities:N0} entities, {buckets:N0} shared keys, " +
                          $"{profile} sizes, {cycles} warm batches, ~{touchCount} keys touched per batch");
        Console.WriteLine($"Largest bucket: {sizes.Max():N0} entities " +
                          $"({(long)sizes.Max() * IntPtr.Size / 1024.0:F0} KB as a reference array); " +
                          $"{lohBuckets} bucket(s) past the 85 KB LOH threshold. " +
                          $"GC: {(System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation")}.");
        Console.WriteLine();
        Console.WriteLine("| Approach | Heap after build | LOH after build | Batch median | Batch max | Alloc/batch | Gen0/1/2 | LOH after cycles (uncompacted) | LOH after Gen2+compact | Gen2+compact pause | Heap growth (dropped) | Extra retained by held snapshot |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|");

        foreach (string approach in Approaches)
        {
            RunApproach(approach, sizes, entities, touchCount, weighted, cycles);
        }

        Console.WriteLine();
        Console.WriteLine("Reading guide: 'LOH after cycles (uncompacted)' is what the process actually keeps " +
                          "resident between batches without a compacting Gen2 (which .NET does not run on its own " +
                          "unless pressured); the compacted column is the live large-object bytes. 'Extra retained' " +
                          "is the heap cost of pinning the pre-cycle snapshot through all batches.");
    }

    private static void RunApproach(
        string approach, int[] sizes, int entities, int touchCount, bool weighted, int cycles)
    {
        ForceCompactingGc();
        long heap0 = GC.GetTotalMemory(forceFullCollection: true);
        long loh0 = LohBytes();

        var store = BuildStore(approach, sizes, entities);
        ForceCompactingGc();
        long heapBuilt = GC.GetTotalMemory(forceFullCollection: true) - heap0;
        long lohBuilt = LohBytes() - loh0;

        object held = store.CaptureSnapshot();

        var rng = new Random(42);
        long nextId = entities;
        var times = new List<double>();
        long allocated = 0;
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var sw = new Stopwatch();
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            var batch = BucketWorkload.BuildBatch(sizes, touchCount, weighted, rng, ref nextId, cycle);
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            store.ApplyBatch(batch);
            sw.Stop();
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;

        // Uncompacted LOH: a full blocking but non-compacting collection releases dead segments'
        // objects yet leaves the LOH fragmented — this is the footprint a service actually carries.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        long lohUncompacted = LohBytes() - loh0;

        sw.Restart();
        ForceCompactingGc();
        sw.Stop();
        double compactPauseMs = sw.Elapsed.TotalMilliseconds;
        long heapHeld = GC.GetTotalMemory(forceFullCollection: true);
        long lohCompacted = LohBytes() - loh0;

        GC.KeepAlive(held);
        held = null!;
        ForceCompactingGc();
        long heapFinal = GC.GetTotalMemory(forceFullCollection: true);
        long retainedByHolding = heapHeld - heapFinal;
        long heapGrowth = heapFinal - heap0 - heapBuilt;

        Console.WriteLine(
            $"| {approach} | {Mib(heapBuilt)} | {Mib(lohBuilt)} | {Median(times):F1} ms | {times.Max():F1} ms " +
            $"| {Mib(allocated / cycles)} | {gen0}/{gen1}/{gen2} | {Mib(lohUncompacted)} | {Mib(lohCompacted)} " +
            $"| {compactPauseMs:F0} ms | {Mib(heapGrowth)} | {Mib(retainedByHolding)} |");

        GC.KeepAlive(store);
    }

    private static string Mib(long bytes) => $"{bytes / 1_048_576.0:F1} MiB";

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static void ForceCompactingGc()
    {
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
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
    }

    private static IBucketStore BuildStore(string approach, int[] sizes, int entities)
    {
        var pristine = BucketWorkload.BuildBuckets(sizes);
        return approach switch
        {
            "ImmArray_AddRange" => new ImmArrayStore(pristine),
            "List_Then_PublishArray" => new ListPublishStore(pristine),
            "ChunkedList_Builder" => new ChunkedStore(pristine),
            "SnapshotTable_Rekeyed" => new TableStore(pristine, entities),
            "MultiValue_Table" => new MultiValueStore(pristine),
            _ => throw new ArgumentException(approach),
        };
    }

    /// <summary>One bucket representation: applies a warm batch in its natural idiom (mirroring
    /// the BenchmarkDotNet methods) and exposes a "pre-cycle published snapshot" to hold.</summary>
    private interface IBucketStore
    {
        void ApplyBatch(BucketWorkload.Change[] batch);
        object CaptureSnapshot();
    }

    private sealed class ImmArrayStore(Entity[][] pristine) : IBucketStore
    {
        private readonly ImmutableArray<Entity>[] _buckets =
            pristine.Select(ImmutableCollectionsMarshal.AsImmutableArray).ToArray();

        public object CaptureSnapshot() => _buckets.Clone();

        public void ApplyBatch(BucketWorkload.Change[] batch)
        {
            foreach (var change in batch)
            {
                var source = _buckets[change.GroupId];
                if (change.Replacements.Length == 0)
                {
                    _buckets[change.GroupId] = source.AddRange(change.Appends);
                }
                else
                {
                    var dst = new Entity[source.Length];
                    source.CopyTo(dst);
                    foreach (var (index, value) in change.Replacements)
                    {
                        dst[index] = value;
                    }
                    _buckets[change.GroupId] = ImmutableCollectionsMarshal.AsImmutableArray(dst);
                }
            }
        }
    }

    private sealed class ListPublishStore : IBucketStore
    {
        private readonly List<Entity>[] _master;
        private readonly ImmutableArray<Entity>[] _published;

        public ListPublishStore(Entity[][] pristine)
        {
            _master = pristine.Select(b => new List<Entity>(b)).ToArray();
            // Initial publish reuses the pristine arrays (same one-array-per-bucket shape the
            // pattern republishes after every batch).
            _published = pristine.Select(ImmutableCollectionsMarshal.AsImmutableArray).ToArray();
        }

        public object CaptureSnapshot() => _published.Clone();

        public void ApplyBatch(BucketWorkload.Change[] batch)
        {
            foreach (var change in batch)
            {
                var list = _master[change.GroupId];
                foreach (var (index, value) in change.Replacements)
                {
                    list[index] = value;
                }
                list.AddRange(change.Appends);
                _published[change.GroupId] = ImmutableCollectionsMarshal.AsImmutableArray(list.ToArray());
            }
        }
    }

    private sealed class ChunkedStore(Entity[][] pristine) : IBucketStore
    {
        private readonly ChunkedImmutableList<Entity>[] _buckets =
            pristine.Select(b => ChunkedImmutableList<Entity>.CreateRange(b)).ToArray();

        public object CaptureSnapshot() => _buckets.Clone();

        public void ApplyBatch(BucketWorkload.Change[] batch)
        {
            foreach (var change in batch)
            {
                var builder = _buckets[change.GroupId].ToBuilder();
                foreach (var (index, value) in change.Replacements)
                {
                    builder[index] = value;
                }
                foreach (var entity in change.Appends)
                {
                    builder.Add(entity);
                }
                _buckets[change.GroupId] = builder.ToImmutable();
            }
        }
    }

    private sealed class MultiValueStore : IBucketStore
    {
        private readonly MultiValueSnapshotTable<long, Entity> _table;

        public MultiValueStore(Entity[][] pristine)
        {
            _table = new MultiValueSnapshotTable<long, Entity>(keyCapacityHint: pristine.Length);
            _table.Reset(pristine.Select((b, g) => KeyValuePair.Create((long)g, (IReadOnlyList<Entity>)b)));
        }

        public object CaptureSnapshot() => _table.GetSnapshot();

        public void ApplyBatch(BucketWorkload.Change[] batch)
        {
            var changes = new BucketChange<long, Entity>[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                var change = batch[i];
                changes[i] = change.Appends.Length > 0
                    ? BucketChange.Append((long)change.GroupId, change.Appends)
                    : BucketChange.ReplaceAt((long)change.GroupId, change.Replacements);
            }
            _table.ApplyChanges(changes);
        }
    }

    private sealed class TableStore : IBucketStore
    {
        private readonly SnapshotTable<(long GroupId, long EntityId), Entity> _table;

        public TableStore(Entity[][] pristine, int entities)
        {
            _table = new SnapshotTable<(long, long), Entity>(capacityHint: entities);
            _table.Reset(pristine.SelectMany(b => b.Select(e => KeyValuePair.Create((e.GroupId, e.Id), e))));
        }

        public object CaptureSnapshot() => _table.GetSnapshot();

        public void ApplyBatch(BucketWorkload.Change[] batch)
        {
            var flat = new List<KeyValuePair<(long, long), Entity>>();
            foreach (var change in batch)
            {
                foreach (var entity in change.Appends)
                {
                    flat.Add(KeyValuePair.Create(((long)change.GroupId, entity.Id), entity));
                }
                foreach (var (_, value) in change.Replacements)
                {
                    flat.Add(KeyValuePair.Create(((long)change.GroupId, value.Id), value));
                }
            }
            _table.ApplyChanges(flat);
        }
    }
}
