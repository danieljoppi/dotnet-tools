using System.Collections.Immutable;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using DotnetTools.SnapshotCache;
using Entity = DotnetTools.SnapshotCache.Benchmarks.BucketWorkload.Entity;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// The read side of the shared-key bucket workloads (see ADR-0005: read performance is
/// first-class — the LOH wins of §9 must not hide an unmeasured read regression). Same
/// populations as <see cref="SharedKeyBucketBenchmarks"/>; three access patterns:
/// <list type="bullet">
///   <item><b>Random indexed reads</b> (10k probes, sampled ∝ bucket size so hot buckets
///   dominate, like real traffic): contiguous <c>ImmutableArray</c> indexing vs
///   <c>ChunkedImmutableList</c>'s three-array-hop indexing.</item>
///   <item><b>Full scan of every bucket</b> (N entities): the report/aggregation path, where
///   array contiguity and prefetching should show their largest advantage over chunk hops.</item>
///   <item><b>Rekeyed table point lookups</b>: 10k <c>(groupId, entityId)</c> probes against a
///   <see cref="SnapshotTable{TKey,TValue}.TableSnapshot"/> (group <i>enumeration</i> would need
///   the secondary index — blocked on large buckets by #9, so not measured here).</item>
/// </list>
/// Read-only: no per-iteration state restore, so timings run at normal invocation counts and are
/// far more stable than the write-side tables.
/// </summary>
[MemoryDiagnoser]
public class BucketReadBenchmarks
{
    private const int Probes = 10_000;

    [Params(1_000_000)]
    public int N;

    [Params(10_000)]
    public int K;

    [Params("Uniform", "Zipf")]
    public string Skew = "Uniform";

    private ImmutableArray<Entity>[] _arrays = null!;
    private ChunkedImmutableList<Entity>[] _chunked = null!;
    private SnapshotTable<(long GroupId, long EntityId), Entity>.TableSnapshot _snapshot = null!;
    private TableIndex<(long GroupId, long EntityId), Entity, long> _byGroup = null!;
    private MultiValueSnapshotTable<long, Entity>.TableSnapshot _multiValueSnapshot = null!;
    private (int Group, int Index)[] _probes = null!;
    private (long, long)[] _probeKeys = null!;

    [GlobalSetup]
    public void Setup()
    {
        var profile = Skew == "Zipf" ? BucketWorkload.SizeProfile.Zipf : BucketWorkload.SizeProfile.Uniform;
        var sizes = BucketWorkload.BuildSizes(profile, K, N);
        var pristine = BucketWorkload.BuildBuckets(sizes);

        _arrays = pristine.Select(ImmutableCollectionsMarshal.AsImmutableArray).ToArray();
        _chunked = pristine.Select(b => ChunkedImmutableList<Entity>.CreateRange(b)).ToArray();

        var table = new SnapshotTable<(long, long), Entity>(capacityHint: N);
        _byGroup = table.CreateIndex((_, e) => e.GroupId);
        table.Reset(pristine.SelectMany(b => b.Select(e => KeyValuePair.Create((e.GroupId, e.Id), e))));
        _snapshot = table.GetSnapshot();

        var multiValue = new MultiValueSnapshotTable<long, Entity>(keyCapacityHint: K);
        multiValue.Reset(pristine.Select((b, g) => KeyValuePair.Create((long)g, (IReadOnlyList<Entity>)b)));
        _multiValueSnapshot = multiValue.GetSnapshot();

        // Probes sampled uniformly over entities (∝ bucket size): a random global position maps
        // to its bucket + local index, so under skew the hot buckets absorb most reads.
        long total = 0;
        var cumulative = new long[K];
        for (int g = 0; g < K; g++)
        {
            total += sizes[g];
            cumulative[g] = total;
        }
        var rng = new Random(7);
        _probes = new (int, int)[Probes];
        _probeKeys = new (long, long)[Probes];
        for (int i = 0; i < Probes; i++)
        {
            long point = rng.NextInt64(total);
            int g = Array.BinarySearch(cumulative, point + 1);
            if (g < 0)
            {
                g = ~g;
            }
            int local = (int)(point - (cumulative[g] - sizes[g]));
            _probes[i] = (g, local);
            _probeKeys[i] = (g, pristine[g][local].Id);
        }
    }

    [Benchmark(Baseline = true, Description = "ImmArray[i] x10k (hot-weighted)")]
    public long ImmArray_RandomIndex()
    {
        long sum = 0;
        foreach (var (group, index) in _probes)
        {
            sum += _arrays[group][index].Kind;
        }
        return sum;
    }

    [Benchmark(Description = "ChunkedList[i] x10k (hot-weighted)")]
    public long ChunkedList_RandomIndex()
    {
        long sum = 0;
        foreach (var (group, index) in _probes)
        {
            sum += _chunked[group][index].Kind;
        }
        return sum;
    }

    [Benchmark(Description = "SnapshotTable_Rekeyed lookup x10k")]
    public long SnapshotTable_PointLookups()
    {
        long sum = 0;
        foreach (var key in _probeKeys)
        {
            _snapshot.TryGetValue(key, out var entity);
            sum += entity.Kind;
        }
        return sum;
    }

    [Benchmark(Description = "ImmArray scan all buckets (1M entities)")]
    public long ImmArray_ScanAllBuckets()
    {
        long sum = 0;
        foreach (var bucket in _arrays)
        {
            foreach (var entity in bucket)
            {
                sum += entity.Kind;
            }
        }
        return sum;
    }

    [Benchmark(Description = "ChunkedList scan all buckets (1M entities)")]
    public long ChunkedList_ScanAllBuckets()
    {
        long sum = 0;
        foreach (var bucket in _chunked)
        {
            foreach (var entity in bucket)
            {
                sum += entity.Kind;
            }
        }
        return sum;
    }

    [Benchmark(Description = "MultiValueTable bucket[i] x10k (hot-weighted)")]
    public long MultiValue_RandomIndex()
    {
        // Buckets come back as IReadOnlyList<Entity> (array or chunked behind the interface), so
        // this pays interface dispatch per element — the honest access path of the packaged type.
        long sum = 0;
        foreach (var (group, index) in _probes)
        {
            sum += _multiValueSnapshot.Lookup(group)[index].Kind;
        }
        return sum;
    }

    [Benchmark(Description = "MultiValueTable scan all buckets (1M entities)")]
    public long MultiValue_ScanAllBuckets()
    {
        long sum = 0;
        for (long g = 0; g < K; g++)
        {
            var bucket = _multiValueSnapshot.Lookup(g);
            for (int i = 0; i < bucket.Count; i++)
            {
                sum += bucket[i].Kind;
            }
        }
        return sum;
    }

    [Benchmark(Description = "SnapshotTable group scan via index (1M entities)")]
    public long SnapshotTable_ScanAllGroupsViaIndex()
    {
        // The rekeyed table's group-enumeration path (hybrid index buckets, issue #9):
        // index key → primary keys → row lookups, all against one consistent snapshot.
        long sum = 0;
        for (long g = 0; g < K; g++)
        {
            foreach (var key in _snapshot.Lookup(_byGroup, g))
            {
                _snapshot.TryGetValue(key, out var entity);
                sum += entity.Kind;
            }
        }
        return sum;
    }
}
