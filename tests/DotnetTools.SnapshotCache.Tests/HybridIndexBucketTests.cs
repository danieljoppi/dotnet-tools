using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>
/// Hybrid secondary-index buckets (issue #9): small buckets stay flat arrays; past
/// <c>ArrayBucketMaxLength</c> elements a bucket is promoted to a <see cref="ChunkedImmutableList{T}"/>,
/// so hot one-to-many groups update at O(chunk) cost and never become LOH arrays.
/// </summary>
public class HybridIndexBucketTests
{
    private sealed record Entity(long Id, long GroupId, int Version);

    private const int Promote = 1024; // keep in sync with IndexState.ArrayBucketMaxLength

    [Fact]
    public void HotBucket_GrowsThroughPromotion_AndLookupStaysConsistent()
    {
        var table = new SnapshotTable<long, Entity>(capacityHint: 8 * Promote);
        var byGroup = table.CreateIndex((_, e) => e.GroupId);

        // Grow one group far past the promotion threshold in warm batches, one other group small.
        long id = 0;
        for (int batch = 0; batch < 40; batch++)
        {
            var upserts = Enumerable.Range(0, 100)
                .Select(_ => KeyValuePair.Create(id, new Entity(id++, 7, batch)))
                .Append(KeyValuePair.Create((long)(1_000_000 + batch), new Entity(1_000_000 + batch, 9, batch)))
                .ToArray();
            table.ApplyChanges(upserts);
        }

        var snapshot = table.GetSnapshot();
        var hot = snapshot.Lookup(byGroup, 7L);
        var small = snapshot.Lookup(byGroup, 9L);
        Assert.Equal(4000, hot.Count);
        Assert.Equal(40, small.Count);
        Assert.Equal(
            Enumerable.Range(0, 4000).Select(i => (long)i).Order().ToArray(),
            hot.Order().ToArray());
        // Every indexed key resolves against the same snapshot.
        foreach (var key in hot)
        {
            Assert.True(snapshot.TryGetValue(key, out var entity));
            Assert.Equal(7L, entity.GroupId);
        }
    }

    [Fact]
    public void PromotedBucket_RemovesAndGroupMoves_MatchSnapshotScan()
    {
        var table = new SnapshotTable<long, Entity>(capacityHint: 4 * Promote);
        var byGroup = table.CreateIndex((_, e) => e.GroupId);
        table.Reset(Enumerable.Range(0, 3 * Promote).Select(i =>
            KeyValuePair.Create((long)i, new Entity(i, i % 2, 0))));

        var rng = new Random(41);
        for (int batch = 0; batch < 20; batch++)
        {
            // Mix: removes from the promoted buckets, moves between groups, inserts.
            var removes = Enumerable.Range(0, 25).Select(_ => (long)rng.Next(3 * Promote)).ToArray();
            var moves = Enumerable.Range(0, 25)
                .Select(_ => (long)rng.Next(3 * Promote))
                .Select(k => KeyValuePair.Create(k, new Entity(k, rng.Next(3), batch)))
                .ToArray();
            table.ApplyChanges(moves, removes);

            var snapshot = table.GetSnapshot();
            for (long g = 0; g < 3; g++)
            {
                var indexed = snapshot.Lookup(byGroup, g).Order().ToArray();
                var scanned = snapshot.Where(kv => kv.Value.GroupId == g).Select(kv => kv.Key).Order().ToArray();
                Assert.Equal(scanned, indexed);
            }
        }
    }

    [Fact]
    public void ManyChangesToOnePromotedBucketInOneBatch_LandAtomicallyThroughOneBuilder()
    {
        // A single batch that appends, removes, and moves against the same promoted bucket must
        // fold through the writer's per-bucket builder (one publish at freeze, not one per change)
        // and still match a from-scratch scan.
        var table = new SnapshotTable<long, Entity>(capacityHint: 8 * Promote);
        var byGroup = table.CreateIndex((_, e) => e.GroupId);
        table.Reset(Enumerable.Range(0, 3 * Promote).Select(i =>
            KeyValuePair.Create((long)i, new Entity(i, 7, 0))));

        var upserts = Enumerable.Range(3 * Promote, 500)                       // 500 appends
            .Select(i => KeyValuePair.Create((long)i, new Entity(i, 7, 1)))
            .Concat(Enumerable.Range(0, 100)                                   // 100 moves out
                .Select(i => KeyValuePair.Create((long)i, new Entity(i, 9, 1))))
            .ToArray();
        var removes = Enumerable.Range(200, 150).Select(i => (long)i).ToArray(); // 150 removes
        table.ApplyChanges(upserts, removes);

        var snapshot = table.GetSnapshot();
        Assert.Equal(3 * Promote + 500 - 100 - 150, snapshot.Lookup(byGroup, 7L).Count);
        for (long g = 7; g <= 9; g += 2)
        {
            Assert.Equal(
                snapshot.Where(kv => kv.Value.GroupId == g).Select(kv => kv.Key).Order().ToArray(),
                snapshot.Lookup(byGroup, g).Order().ToArray());
        }
    }

    [Fact]
    public void PromotedBucket_EmptiedInOneBatch_RemovesTheIndexKey()
    {
        var table = new SnapshotTable<long, Entity>(capacityHint: 4 * Promote);
        var byGroup = table.CreateIndex((_, e) => e.GroupId);
        table.Reset(Enumerable.Range(0, 2 * Promote).Select(i =>
            KeyValuePair.Create((long)i, new Entity(i, 5, 0))));
        Assert.Equal(2 * Promote, table.GetSnapshot().Lookup(byGroup, 5L).Count);

        table.ApplyChanges(null, Enumerable.Range(0, 2 * Promote).Select(i => (long)i));

        Assert.Empty(table.GetSnapshot().Lookup(byGroup, 5L));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void HotBucketAppends_DoNotTouchTheLargeObjectHeap()
    {
        var table = new SnapshotTable<long, Entity>(capacityHint: 200_000);
        var byGroup = table.CreateIndex((_, e) => e.GroupId);
        // Seed one group already far past the old array design's LOH bar (~10,625 8-byte keys).
        table.Reset(Enumerable.Range(0, 60_000).Select(i =>
            KeyValuePair.Create((long)i, new Entity(i, 1, 0))));

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long lohBefore = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[3].SizeAfterBytes;

        // Warm appends into the hot group — the pattern that previously copied an
        // LOH-sized key array per appended entity.
        long id = 60_000;
        for (int batch = 0; batch < 20; batch++)
        {
            table.ApplyChanges(Enumerable.Range(0, 50)
                .Select(_ => KeyValuePair.Create(id, new Entity(id++, 1, batch))).ToArray());
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long lohAfter = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[3].SizeAfterBytes;
        Assert.True(lohAfter - lohBefore < 85_000,
            $"LOH grew by {lohAfter - lohBefore} bytes during hot-bucket index appends");
        Assert.Equal(61_000, table.GetSnapshot().Lookup(byGroup, 1L).Count);
    }
}
