using System.Runtime.CompilerServices;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>
/// Correctness of the shared-key → many-values shape (one-to-many buckets, skewed sizes past the
/// LOH threshold): structural sharing of old bucket snapshots, atomic batches under concurrent
/// readers, LOH-safe chunk sizing under heavy skewed appends, long-held snapshot stability, and
/// secondary-index consistency for group lookups.
/// </summary>
public class SharedKeyBucketTests
{
    private sealed record Entity(long Id, long GroupId, int Version, string Label);

    private static Entity MakeEntity(long groupId, long id, int version) =>
        new(id, groupId, version, $"entity-{id}");

    [Fact]
    public void BucketMutation_LeavesOldSnapshotUntouchedAndSharesUntouchedChunks()
    {
        // A bucket large enough for many chunks (reference elements → default 512-row chunks).
        var bucket = ChunkedImmutableList<Entity>.CreateRange(
            Enumerable.Range(0, 20_000).Select(i => MakeEntity(1, i, 0)));
        var before = bucket.ToArray();

        // Warm update: append a small run and replace a slice in the middle — the workload-B shape.
        var builder = bucket.ToBuilder();
        for (int i = 0; i < 25; i++)
        {
            builder.Add(MakeEntity(1, 100_000 + i, 1));
        }
        for (int i = 5_000; i < 5_000 + 200; i++)
        {
            builder[i] = MakeEntity(1, i, 1);
        }
        var updated = builder.ToImmutable();

        // Old snapshot completely unchanged.
        Assert.Equal(20_000, bucket.Count);
        Assert.Equal(before, bucket.ToArray());
        Assert.Equal(20_025, updated.Count);
        Assert.Equal(1, updated[5_100].Version);
        Assert.Equal(0, bucket[5_100].Version);

        // And almost all chunk arrays are shared, not copied: only the replaced slice's chunks,
        // the append-tail chunk, and the touched spine block may differ.
        int shared = 0, total = 0;
        var oldBlocks = bucket.UnsafeBlocks;
        var newBlocks = updated.UnsafeBlocks;
        int oldChunks = (bucket.Count - 1) / bucket.ChunkRows + 1;
        for (int c = 0; c < oldChunks; c++)
        {
            total++;
            if (ReferenceEquals(
                    oldBlocks[c >> 10][c & 1023],
                    newBlocks[c >> 10][c & 1023]))
            {
                shared++;
            }
        }
        int touchedChunks = 200 / bucket.ChunkRows + 3; // replaced slice + boundary + append tail
        Assert.True(shared >= total - touchedChunks,
            $"expected ≥{total - touchedChunks} of {total} chunks shared, got {shared}");
    }

    [Fact]
    public async Task ApplyChanges_GroupBatchIsAtomic_ReaderNeverSeesMixedVersions()
    {
        // Buckets flattened to (groupId, entityId) → entity. Every batch rewrites all entities of
        // one group to the same new version; a consistent snapshot must never mix versions
        // within a group.
        const int groups = 8;
        const int perGroup = 100;
        var table = new SnapshotTable<(long Group, long Id), Entity>(capacityHint: groups * perGroup);
        table.Reset(
            from g in Enumerable.Range(0, groups)
            from i in Enumerable.Range(0, perGroup)
            select KeyValuePair.Create(((long)g, (long)(g * perGroup + i)), MakeEntity(g, g * perGroup + i, 0)));

        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 2).Select(r => Task.Run(() =>
        {
            var rng = new Random(100 + r);
            while (!stop.IsCancellationRequested)
            {
                int g = rng.Next(groups);
                var snapshot = table.GetSnapshot();
                int version = -1;
                for (int i = 0; i < perGroup; i++)
                {
                    Assert.True(snapshot.TryGetValue(((long)g, (long)(g * perGroup + i)), out var entity));
                    if (version < 0)
                    {
                        version = entity.Version;
                    }
                    else
                    {
                        Assert.Equal(version, entity.Version);
                    }
                }
            }
        })).ToArray();

        var writerRng = new Random(7);
        for (int version = 1; version <= 500; version++)
        {
            int g = writerRng.Next(groups);
            table.ApplyChanges(Enumerable.Range(0, perGroup).Select(i =>
                KeyValuePair.Create(((long)g, (long)(g * perGroup + i)), MakeEntity(g, g * perGroup + i, version))));
        }

        stop.Cancel();
        await Task.WhenAll(readers);
    }

    [Fact]
    public void SkewedAppend_NoBackingArrayEverReachesLohThreshold()
    {
        // Grow one hot bucket from empty to 120k entities via warm 1–50 appends (the Zipf head),
        // checking that no chunk array can reach 85,000 bytes at any point.
        var bucket = ChunkedImmutableList<Entity>.Empty;
        var rng = new Random(42);
        long id = 0;
        while (bucket.Count < 120_000)
        {
            var builder = bucket.ToBuilder();
            int appends = rng.Next(1, 51);
            for (int i = 0; i < appends; i++)
            {
                builder.Add(MakeEntity(0, id++, 0));
            }
            bucket = builder.ToImmutable();
        }

        long chunkPayloadBytes = (long)bucket.ChunkRows * IntPtr.Size;
        Assert.True(chunkPayloadBytes < 85_000, $"chunk payload {chunkPayloadBytes} bytes");

        // Walk every allocated backing array (chunks, spine blocks, top spine) and bound its size.
        var blocks = bucket.UnsafeBlocks;
        Assert.True((long)blocks.Length * IntPtr.Size < 85_000, "top spine reached LOH size");
        foreach (var block in blocks)
        {
            if (block is null)
            {
                continue;
            }
            Assert.True((long)block.Length * IntPtr.Size < 85_000, "spine block reached LOH size");
            foreach (var chunk in block)
            {
                if (chunk is not null)
                {
                    Assert.True((long)chunk.Length * IntPtr.Size < 85_000,
                        $"chunk of {chunk.Length} references reached LOH size");
                }
            }
        }

        // Same guarantee for the flattened SnapshotTable rows (KeyValuePair<(long,long), Entity>).
        int rowChunkRows = ChunkedImmutableList<KeyValuePair<(long, long), Entity>>.DefaultChunkRows;
        long rowChunkBytes = (long)rowChunkRows * Unsafe.SizeOf<KeyValuePair<(long, long), Entity>>();
        Assert.True(rowChunkBytes < 85_000, $"row chunk {rowChunkBytes} bytes");
    }

    [Fact]
    public void HeldSnapshot_StaysConsistentAcrossManyBatches()
    {
        const int groups = 50;
        const int perGroup = 200;
        var table = new SnapshotTable<(long Group, long Id), Entity>(capacityHint: groups * perGroup);
        table.Reset(
            from g in Enumerable.Range(0, groups)
            from i in Enumerable.Range(0, perGroup)
            select KeyValuePair.Create(((long)g, (long)(g * perGroup + i)), MakeEntity(g, g * perGroup + i, 0)));

        var held = table.GetSnapshot();
        int heldCount = held.Count;

        var rng = new Random(11);
        long nextId = groups * perGroup;
        for (int version = 1; version <= 100; version++)
        {
            // Mixed batch: replace a run in one group, append to another, remove from a third.
            int replaceGroup = rng.Next(groups);
            int appendGroup = rng.Next(groups);
            var upserts = Enumerable.Range(0, 50)
                .Select(i => KeyValuePair.Create(
                    ((long)replaceGroup, (long)(replaceGroup * perGroup + i)),
                    MakeEntity(replaceGroup, replaceGroup * perGroup + i, version)))
                .Append(KeyValuePair.Create(((long)appendGroup, nextId), MakeEntity(appendGroup, nextId, version)))
                .ToArray();
            nextId++;
            table.ApplyChanges(upserts, version % 10 == 0 ? [((long)rng.Next(groups), (long)rng.Next(perGroup))] : null);
        }

        // The held snapshot still shows the original world, in full.
        Assert.Equal(heldCount, held.Count);
        for (int g = 0; g < groups; g++)
        {
            for (int i = 0; i < perGroup; i++)
            {
                Assert.True(held.TryGetValue(((long)g, (long)(g * perGroup + i)), out var entity));
                Assert.Equal(0, entity.Version);
            }
        }
    }

    [Fact]
    public void SecondaryIndex_GroupLookups_MatchSnapshotScanAfterBatches()
    {
        const int groups = 20;
        const int perGroup = 100;
        var table = new SnapshotTable<long, Entity>(capacityHint: groups * perGroup);
        var byGroup = table.CreateIndex((_, entity) => entity.GroupId);
        table.Reset(
            from g in Enumerable.Range(0, groups)
            from i in Enumerable.Range(0, perGroup)
            let id = (long)(g * perGroup + i)
            select KeyValuePair.Create(id, MakeEntity(g, id, 0)));

        var rng = new Random(23);
        long nextId = groups * perGroup;
        for (int version = 1; version <= 30; version++)
        {
            // Appends into random groups, moves between groups, value-only updates, removes.
            var upserts = new List<KeyValuePair<long, Entity>>
            {
                KeyValuePair.Create(nextId, MakeEntity(rng.Next(groups), nextId, version)),
                KeyValuePair.Create((long)rng.Next(groups * perGroup), MakeEntity(rng.Next(groups), rng.Next(groups * perGroup), version)),
            };
            nextId++;
            var removes = new[] { (long)rng.Next(groups * perGroup) };
            table.ApplyChanges(upserts.Select(kv => KeyValuePair.Create(kv.Key, kv.Value with { Id = kv.Key })), removes);

            var snapshot = table.GetSnapshot();
            for (int g = 0; g < groups; g++)
            {
                var indexed = snapshot.Lookup(byGroup, (long)g).Order().ToArray();
                var scanned = snapshot.Where(kv => kv.Value.GroupId == g).Select(kv => kv.Key).Order().ToArray();
                Assert.Equal(scanned, indexed);
            }
        }
    }
}
