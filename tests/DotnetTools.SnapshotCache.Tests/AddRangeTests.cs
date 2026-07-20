using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>Bulk append (<see cref="ChunkedImmutableList{T}.Builder.AddRange(ReadOnlySpan{T})"/>)
/// and the public bytes-based chunk sizing (issue #10).</summary>
public class AddRangeTests
{
    [Fact]
    public void AddRange_Span_MatchesPerItemAdd_AcrossChunkAndSpineBoundaries()
    {
        int chunk = ChunkedImmutableList<long>.DefaultChunkRows;
        int size = chunk * 5 + 37; // several chunk crossings + partial tail
        var items = Enumerable.Range(0, size).Select(i => (long)i).ToArray();

        var perItem = ChunkedImmutableList<long>.Empty.ToBuilder();
        foreach (long item in items)
        {
            perItem.Add(item);
        }
        var bulk = ChunkedImmutableList<long>.Empty.ToBuilder();
        bulk.AddRange(items.AsSpan());

        Assert.Equal(perItem.ToImmutable().ToArray(), bulk.ToImmutable().ToArray());
    }

    [Fact]
    public void AddRange_OntoPublishedTrimmedTail_ExpandsAndRoundTrips()
    {
        var list = ChunkedImmutableList<long>.CreateRange(Enumerable.Range(0, 10).Select(i => (long)i));
        var builder = list.ToBuilder();
        builder.AddRange(Enumerable.Range(10, 3000).Select(i => (long)i).ToArray());
        var grown = builder.ToImmutable();

        Assert.Equal(3010, grown.Count);
        Assert.Equal(3009L, grown[3009]);
        Assert.Equal(10, list.Count); // original snapshot untouched
        Assert.Equal(10, list.UnsafeBlocks[0][0].Length);
    }

    [Fact]
    public void AddRange_Enumerable_TakesAllPaths()
    {
        var expected = Enumerable.Range(0, 1500).Select(i => (long)i).ToArray();

        var fromArray = ChunkedImmutableList<long>.Empty.ToBuilder();
        fromArray.AddRange(expected);
        var fromList = ChunkedImmutableList<long>.Empty.ToBuilder();
        fromList.AddRange(expected.ToList());
        var fromLazy = ChunkedImmutableList<long>.Empty.ToBuilder();
        fromLazy.AddRange(expected.Select(x => x)); // no span fast path

        Assert.Equal(expected, fromArray.ToImmutable().ToArray());
        Assert.Equal(expected, fromList.ToImmutable().ToArray());
        Assert.Equal(expected, fromLazy.ToImmutable().ToArray());

        var empty = ChunkedImmutableList<long>.Empty.ToBuilder();
        empty.AddRange(ReadOnlySpan<long>.Empty);
        Assert.Equal(0, empty.Count);
        Assert.Throws<ArgumentNullException>(() => empty.AddRange((IEnumerable<long>)null!));
    }

    [Fact]
    public void EmptyWithTargetBytes_SizesChunksFromElementSize()
    {
        Assert.Equal(2048, ChunkedImmutableList<long>.EmptyWithTargetBytes(16 * 1024).ChunkRows);
        Assert.Equal(1024, ChunkedImmutableList<Guid>.EmptyWithTargetBytes(16 * 1024).ChunkRows);
        // Chunk size is preserved through builders and stays LOH-safe at the cap.
        var list = ChunkedImmutableList<long>.EmptyWithTargetBytes(64 * 1024);
        var builder = list.ToBuilder();
        builder.AddRange(Enumerable.Range(0, 10_000).Select(i => (long)i).ToArray());
        var built = builder.ToImmutable();
        Assert.Equal(list.ChunkRows, built.ChunkRows);
        Assert.Equal(9_999L, built[9_999]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedImmutableList<long>.EmptyWithTargetBytes(0));
    }
}
