using System.Collections.Immutable;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using DotnetTools.SnapshotCache;
using Entity = DotnetTools.SnapshotCache.Benchmarks.BucketWorkload.Entity;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Shared harness for the shared-key → many-values benchmarks: <see cref="BucketCount"/> buckets
/// holding <see cref="EntityCount"/> entities total, warmed up, then one batch of appends
/// (1–50 entities per touched key) and replacements (~1% of a touched key's bucket) applied by
/// four bucket representations:
/// <list type="bullet">
///   <item><c>ImmArray_AddRange</c> — <c>ImmutableArray</c> per key; every change copies the
///   whole bucket array (LOH-sized for hot buckets).</item>
///   <item><c>List_Then_PublishArray</c> — mutable <c>List</c> master mutated in place, then one
///   array materialization per touched key to publish an immutable view.</item>
///   <item><c>ChunkedList_Builder</c> — <c>ChunkedImmutableList</c> builder → ToImmutable,
///   copying only touched sub-LOH chunks.</item>
///   <item><c>SnapshotTable_Rekeyed</c> — buckets flattened to <c>(groupId, entityId) → entity</c>
///   in one <see cref="SnapshotTable{TKey,TValue}"/>, updated with one ApplyChanges batch.
///   (Per-group enumeration would additionally need a secondary index — see RESULTS.md.)</item>
/// </list>
/// Timings/allocations here; LOH size deltas come from the <c>--bucket-loh</c> console study
/// (BenchmarkDotNet's memory diagnoser doesn't report LOH occupancy). State is restored between
/// iterations, so every invocation applies the identical batch to the identical population.
/// </summary>
[MemoryDiagnoser]
public abstract class BucketBenchmarksBase
{
    protected abstract int EntityCount { get; }
    protected abstract int BucketCount { get; }
    protected abstract BucketWorkload.SizeProfile Profile { get; }
    protected abstract int TouchCount { get; }
    protected abstract bool WeightTouchesBySize { get; }

    private int[] _sizes = null!;
    private Entity[][] _pristine = null!;
    private BucketWorkload.Change[] _batch = null!;

    // Per-approach stores, indexed by group id. All share the same Entity instances; only the
    // bucket containers differ. (A real system maps sharedKey → bucket; that map update is one
    // reference store per touched key for every approach, so it is excluded as equal cost.)
    private ImmutableArray<Entity>[] _immArrays = null!;
    private ImmutableArray<Entity>[] _pristineImmArrays = null!;
    private List<Entity>[] _lists = null!;
    private ImmutableArray<Entity>[] _publishedArrays = null!;
    private ChunkedImmutableList<Entity>[] _chunked = null!;
    private ChunkedImmutableList<Entity>[] _pristineChunked = null!;
    private SnapshotTable<(long GroupId, long EntityId), Entity> _table = null!;
    private KeyValuePair<(long, long), Entity>[] _flatBatch = null!;
    private KeyValuePair<(long, long), Entity>[] _tableRestoreUpserts = null!;
    private (long, long)[] _tableRestoreRemoves = null!;

    [GlobalSetup]
    public void Setup()
    {
        int n = EntityCount;
        int k = BucketCount;
        _sizes = BucketWorkload.BuildSizes(Profile, k, n);
        _pristine = BucketWorkload.BuildBuckets(_sizes);

        long nextId = n;
        _batch = BucketWorkload.BuildBatch(
            _sizes, TouchCount, WeightTouchesBySize, rng: new Random(42), ref nextId, version: 1);

        _pristineImmArrays = new ImmutableArray<Entity>[k];
        _pristineChunked = new ChunkedImmutableList<Entity>[k];
        _lists = new List<Entity>[k];
        for (int g = 0; g < k; g++)
        {
            _pristineImmArrays[g] = ImmutableCollectionsMarshal.AsImmutableArray(_pristine[g]);
            _pristineChunked[g] = ChunkedImmutableList<Entity>.CreateRange(_pristine[g]);
            _lists[g] = new List<Entity>(_pristine[g]);
        }
        _immArrays = (ImmutableArray<Entity>[])_pristineImmArrays.Clone();
        _chunked = (ChunkedImmutableList<Entity>[])_pristineChunked.Clone();
        _publishedArrays = new ImmutableArray<Entity>[k];

        _table = new SnapshotTable<(long, long), Entity>(capacityHint: n);
        _table.Reset(_pristine.SelectMany(b => b.Select(e => KeyValuePair.Create((e.GroupId, e.Id), e))));

        // The same batch flattened to composite keys, plus its inverse for iteration restore.
        _flatBatch = _batch
            .SelectMany(c => c.Appends.Select(e => KeyValuePair.Create(((long)c.GroupId, e.Id), e))
                .Concat(c.Replacements.Select(r => KeyValuePair.Create(((long)c.GroupId, r.Value.Id), r.Value))))
            .ToArray();
        _tableRestoreRemoves = _batch
            .SelectMany(c => c.Appends.Select(e => ((long)c.GroupId, e.Id)))
            .ToArray();
        _tableRestoreUpserts = _batch
            .SelectMany(c => c.Replacements.Select(r =>
                KeyValuePair.Create(((long)c.GroupId, r.Value.Id), _pristine[c.GroupId][r.Index])))
            .ToArray();
    }

    /// <summary>Rolls every store back to the pristine population so each invocation applies the
    /// same batch to the same state. Runs outside the measured region.</summary>
    [IterationSetup]
    public void RestoreState()
    {
        foreach (var change in _batch)
        {
            int g = change.GroupId;
            _immArrays[g] = _pristineImmArrays[g];
            _chunked[g] = _pristineChunked[g];
            var list = _lists[g];
            list.Clear();
            list.AddRange(_pristine[g]);
        }
        _table.ApplyChanges(_tableRestoreUpserts, _tableRestoreRemoves);
    }

    [Benchmark(Description = "ImmArray_AddRange")]
    public long ImmArray_AddRange()
    {
        long touched = 0;
        foreach (var change in _batch)
        {
            var source = _immArrays[change.GroupId];
            ImmutableArray<Entity> next;
            if (change.Replacements.Length == 0)
            {
                next = source.AddRange(change.Appends);
            }
            else
            {
                // Best-case single-copy replace: copy once, patch in place, republish.
                var dst = new Entity[source.Length];
                source.CopyTo(dst);
                foreach (var (index, value) in change.Replacements)
                {
                    dst[index] = value;
                }
                next = ImmutableCollectionsMarshal.AsImmutableArray(dst);
            }
            _immArrays[change.GroupId] = next;
            touched += next.Length;
        }
        return touched;
    }

    [Benchmark(Description = "List_Then_PublishArray")]
    public long List_Then_PublishArray()
    {
        long touched = 0;
        foreach (var change in _batch)
        {
            var list = _lists[change.GroupId];
            foreach (var (index, value) in change.Replacements)
            {
                list[index] = value;
            }
            list.AddRange(change.Appends);
            // Publish one immutable array per touched key — the single O(bucket) allocation.
            var published = ImmutableCollectionsMarshal.AsImmutableArray(list.ToArray());
            _publishedArrays[change.GroupId] = published;
            touched += published.Length;
        }
        return touched;
    }

    [Benchmark(Baseline = true, Description = "ChunkedList_Builder")]
    public long ChunkedList_Builder()
    {
        long touched = 0;
        foreach (var change in _batch)
        {
            var builder = _chunked[change.GroupId].ToBuilder();
            foreach (var (index, value) in change.Replacements)
            {
                builder[index] = value;
            }
            foreach (var entity in change.Appends)
            {
                builder.Add(entity);
            }
            var next = builder.ToImmutable();
            _chunked[change.GroupId] = next;
            touched += next.Count;
        }
        return touched;
    }

    [Benchmark(Description = "SnapshotTable_Rekeyed")]
    public int SnapshotTable_Rekeyed()
    {
        _table.ApplyChanges(_flatBatch);
        return _table.Count;
    }
}

/// <summary>
/// Workload B — the primary shared-key scenario: 1M entities over 10k or 100k shared keys, bucket
/// sizes uniform or Zipf heavy-tail (at K=10k the hottest Zipf bucket holds ~100k entities —
/// ~800 KB as an array of references, ~10× past the LOH threshold). The warm batch touches ~1% of
/// keys, sampled proportionally to bucket size under skew.
/// </summary>
public class SharedKeyBucketBenchmarks : BucketBenchmarksBase
{
    [Params(1_000_000)]
    public int N;

    [Params(10_000, 100_000)]
    public int K;

    [Params("Uniform", "Zipf")]
    public string Skew = "Uniform";

    protected override int EntityCount => N;
    protected override int BucketCount => K;
    protected override BucketWorkload.SizeProfile Profile =>
        Skew == "Zipf" ? BucketWorkload.SizeProfile.Zipf : BucketWorkload.SizeProfile.Uniform;
    protected override int TouchCount => Math.Max(1, K / 100);
    protected override bool WeightTouchesBySize => Skew == "Zipf";
}

/// <summary>
/// Workload C — one single large refresh: ~12k entity operations spread over ~800 of 2,000 keys
/// in one batch, against a population where 15 buckets (30k entities each, ~240 KB as reference
/// arrays) are already past the LOH threshold and the rest hold a few hundred entities.
/// </summary>
public class LargeRefreshBenchmarks : BucketBenchmarksBase
{
    [Params(1_000_000)]
    public int N;

    [Params(2_000)]
    public int K;

    protected override int EntityCount => N;
    protected override int BucketCount => K;
    protected override BucketWorkload.SizeProfile Profile => BucketWorkload.SizeProfile.HeavyTailRefresh;
    protected override int TouchCount => 800;
    protected override bool WeightTouchesBySize => true;
}
