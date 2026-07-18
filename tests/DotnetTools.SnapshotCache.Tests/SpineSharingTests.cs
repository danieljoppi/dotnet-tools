using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>Verifies the two-level spine's structural sharing — the property that makes batch
/// updates O(touched chunks) and old snapshots cheap at 100M+ row scale.</summary>
public class SpineSharingTests
{
    [Fact]
    public void SetItem_SharesAllUntouchedSpineBlocksAndChunks()
    {
        // Enough elements for 3 spine blocks (1024 chunks per block).
        int chunk = ChunkedImmutableList<int>.DefaultChunkRows;
        int size = chunk * ChunkedImmutableList<int>.SpineBlockLength * 2 + 5 * chunk;
        var original = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, size));

        int target = chunk * ChunkedImmutableList<int>.SpineBlockLength + 7; // inside block 1
        var updated = original.SetItem(target, -1);

        var a = original.UnsafeBlocks;
        var b = updated.UnsafeBlocks;
        Assert.Same(a[0], b[0]); // untouched block shared wholesale
        Assert.Same(a[2], b[2]);
        Assert.NotSame(a[1], b[1]); // touched block cloned...
        int touchedChunk = target / chunk % ChunkedImmutableList<int>.SpineBlockLength;
        for (int s = 0; s < a[1].Length; s++)
        {
            if (a[1][s] is null)
            {
                continue;
            }
            if (s == touchedChunk)
            {
                Assert.NotSame(a[1][s], b[1][s]); // ...but only the touched chunk copied
            }
            else
            {
                Assert.Same(a[1][s], b[1][s]);
            }
        }
        Assert.Equal(-1, updated[target]);
        Assert.Equal(target, original[target]);
    }

    [Fact]
    public void Builder_CrossingSpineBlockBoundary_Works()
    {
        int chunk = ChunkedImmutableList<int>.DefaultChunkRows;
        int oneBlock = chunk * ChunkedImmutableList<int>.SpineBlockLength;
        var builder = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, oneBlock - 1)).ToBuilder();

        builder.Add(-10); // fills block 0 exactly
        builder.Add(-20); // first element of block 1
        var list = builder.ToImmutable();

        Assert.Equal(oneBlock + 1, list.Count);
        Assert.Equal(-10, list[oneBlock - 1]);
        Assert.Equal(-20, list[oneBlock]);
        Assert.Equal(2, list.UnsafeBlocks.Length);

        // And back down across the boundary.
        var shrink = list.ToBuilder();
        shrink.RemoveLast();
        shrink.RemoveLast();
        var shrunk = shrink.ToImmutable();
        Assert.Equal(oneBlock - 1, shrunk.Count);
        Assert.Equal(oneBlock - 2, shrunk[oneBlock - 2]);
        Assert.Single(shrunk.UnsafeBlocks);
    }

    [Fact]
    public void CustomChunkRows_IsRespectedAndValidated()
    {
        var list = ChunkedImmutableList<long>.EmptyWithChunkRows(64);
        Assert.Equal(64, list.ChunkRows);
        var builder = list.ToBuilder();
        for (int i = 0; i < 1000; i++)
        {
            builder.Add(i);
        }
        var built = builder.ToImmutable();
        Assert.Equal(64, built.ChunkRows);
        Assert.Equal(999L, built[999]);

        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedImmutableList<long>.EmptyWithChunkRows(100)); // not pow2
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedImmutableList<long>.EmptyWithChunkRows(1 << 20)); // > LOH cap
    }

    [Fact]
    public void SnapshotTable_CustomChunkRows_FlowsThrough()
    {
        var table = new SnapshotTable<long, long>(new SnapshotTableOptions<long>
        {
            CapacityHint = 10_000,
            ChunkRows = 128,
        });
        table.Reset(Enumerable.Range(0, 10_000).Select(i => KeyValuePair.Create((long)i, (long)-i)));
        Assert.Equal(10_000, table.Count);
        Assert.Equal(-9_999L, table[9_999L]);
        Assert.Equal(128, table.GetSnapshot().Rows.ChunkRows);
    }
}
