using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>
/// The compact small-list representation (issue #7): published tail chunks are trimmed to the
/// element count and spine blocks grow by doubling instead of starting at 8 KB, so one
/// <see cref="ChunkedImmutableList{T}"/> per bucket is affordable at high key counts. Reads keep
/// the same branch-free three-indexing path.
/// </summary>
public class CompactRepresentationTests
{
    [Fact]
    public void PublishedTailChunk_IsTrimmedToCount()
    {
        var list = ChunkedImmutableList<long>.CreateRange(Enumerable.Range(0, 10).Select(i => (long)i));
        Assert.Equal(10, list.Count);
        Assert.Equal(10, list.UnsafeBlocks[0][0].Length); // exact, not a full chunk
        Assert.Equal(9L, list[9]);

        // Appending re-expands the working tail and re-trims on publish; the original stays trimmed.
        var grown = list.Add(10);
        Assert.Equal(11, grown.UnsafeBlocks[0][0].Length);
        Assert.Equal(10, list.UnsafeBlocks[0][0].Length);
        Assert.Equal(10L, grown[10]);
        Assert.Equal([.. Enumerable.Range(0, 10).Select(i => (long)i)], list.ToArray());
    }

    [Fact]
    public void FullChunks_AreNotTrimmed_AndTailOnlyPublishCopiesNothingWhenUntouched()
    {
        int chunk = ChunkedImmutableList<long>.DefaultChunkRows;
        var list = ChunkedImmutableList<long>.CreateRange(Enumerable.Range(0, chunk + 5).Select(i => (long)i));
        Assert.Equal(chunk, list.UnsafeBlocks[0][0].Length);
        Assert.Equal(5, list.UnsafeBlocks[0][1].Length);

        // A builder that only touches chunk 0 must republish the trimmed tail by reference.
        var builder = list.ToBuilder();
        builder[0] = -1;
        var next = builder.ToImmutable();
        Assert.Same(list.UnsafeBlocks[0][1], next.UnsafeBlocks[0][1]);
        Assert.NotSame(list.UnsafeBlocks[0][0], next.UnsafeBlocks[0][0]);
    }

    [Fact]
    public void SpineBlocks_StartSmallAndGrowByDoubling()
    {
        int chunk = ChunkedImmutableList<long>.DefaultChunkRows;

        var oneChunk = ChunkedImmutableList<long>.CreateRange(Enumerable.Range(0, chunk).Select(i => (long)i));
        Assert.Equal(4, oneChunk.UnsafeBlocks[0].Length); // initial block, not 1024 refs

        var fiveChunks = ChunkedImmutableList<long>.CreateRange(Enumerable.Range(0, 5 * chunk).Select(i => (long)i));
        Assert.Equal(8, fiveChunks.UnsafeBlocks[0].Length); // doubled 4 → 8

        var fullBlock = ChunkedImmutableList<long>.CreateRange(
            Enumerable.Range(0, chunk * ChunkedImmutableList<long>.SpineBlockLength + 1).Select(i => (long)i));
        Assert.Equal(ChunkedImmutableList<long>.SpineBlockLength, fullBlock.UnsafeBlocks[0].Length);
        Assert.Equal(4, fullBlock.UnsafeBlocks[1].Length);
        int firstOfBlock1 = chunk * ChunkedImmutableList<long>.SpineBlockLength;
        Assert.Equal((long)firstOfBlock1, fullBlock[firstOfBlock1]);
    }

    [Fact]
    public void TrimmedTail_SurvivesBuilderMutationsAgainstModel()
    {
        // Interleave publishes (which trim) with appends, removes, and replacements, comparing to
        // a List<int> model — exercises trimmed-tail re-expansion and shrink-across-trim paths.
        var rng = new Random(99);
        var model = new List<int>();
        var list = ChunkedImmutableList<int>.Empty;
        for (int round = 0; round < 200; round++)
        {
            var builder = list.ToBuilder();
            int ops = rng.Next(1, 40);
            for (int i = 0; i < ops; i++)
            {
                switch (rng.Next(3))
                {
                    case 0:
                        builder.Add(round * 1000 + i);
                        model.Add(round * 1000 + i);
                        break;
                    case 1 when model.Count > 0:
                        builder.RemoveLast();
                        model.RemoveAt(model.Count - 1);
                        break;
                    case 2 when model.Count > 0:
                        int index = rng.Next(model.Count);
                        builder[index] = -round;
                        model[index] = -round;
                        break;
                }
            }
            list = builder.ToImmutable();
            Assert.Equal(model.Count, list.Count);
        }
        Assert.Equal(model, list.ToArray());
    }

    [Fact]
    public void SnapshotTable_SmallTables_KeepTrimmedRows()
    {
        var table = new SnapshotTable<long, long>(capacityHint: 16);
        table.Reset(Enumerable.Range(0, 10).Select(i => KeyValuePair.Create((long)i, (long)-i)));
        var rows = table.GetSnapshot().Rows;
        Assert.Equal(10, rows.Count);
        Assert.Equal(10, rows.UnsafeBlocks[0][0].Length);
        table.ApplyChanges([KeyValuePair.Create(42L, 42L)]);
        Assert.Equal(11, table.GetSnapshot().Rows.UnsafeBlocks[0][0].Length);
        Assert.Equal(42L, table[42L]);
        Assert.Equal(-9L, table[9L]);
    }
}
