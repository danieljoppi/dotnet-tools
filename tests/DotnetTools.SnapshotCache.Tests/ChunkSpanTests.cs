using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>Chunk-span enumeration, <c>CopyTo(Span)</c>, and fast <c>ToArray()</c> (issue #22):
/// scans run as tight span loops, and bulk export goes through chunk-sized block copies. Spans
/// must expose only live elements — the tail is <c>Count</c>-bounded, never spare capacity.</summary>
public class ChunkSpanTests
{
    private static ChunkedImmutableList<int> Range(int n) =>
        ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, n));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]      // single trimmed tail chunk
    [InlineData(512)]     // exactly one default chunk
    [InlineData(513)]     // one full chunk + a 1-element trimmed tail
    [InlineData(5_000)]   // several chunks, partial tail
    public void Chunks_ConcatenationEqualsSequence_AndTailHasNoSpareCapacity(int n)
    {
        var list = Range(n);
        var flattened = new List<int>();
        int chunkCount = 0;
        int chunkRows = list.ChunkRows;
        foreach (var span in list.Chunks)
        {
            Assert.False(span.IsEmpty && n > 0, "no empty chunk should be yielded");
            Assert.True(span.Length <= chunkRows, "chunk span exceeds ChunkRows");
            for (int i = 0; i < span.Length; i++)
            {
                flattened.Add(span[i]);
            }
            chunkCount++;
        }
        Assert.Equal(Enumerable.Range(0, n), flattened);
        // Total live elements across spans == Count (the tail exposes no spare slots).
        Assert.Equal(n, flattened.Count);
        Assert.Equal(n == 0 ? 0 : (n - 1) / chunkRows + 1, chunkCount);
    }

    [Fact]
    public void CopyTo_MatchesToArray_AcrossBoundariesAndTrimmedTails()
    {
        foreach (int n in new[] { 0, 1, 511, 512, 513, 1024, 4_097 })
        {
            var list = Range(n);
            var dst = new int[n];
            list.CopyTo(dst);
            Assert.Equal(Enumerable.Range(0, n).ToArray(), dst);
            Assert.Equal(dst, list.ToArray());       // fast ToArray agrees
            Assert.Equal(dst, list.AsEnumerable().ToArray()); // and with the LINQ walk
        }
    }

    [Fact]
    public void CopyTo_IntoLargerSpan_LeavesTailUntouched()
    {
        var list = Range(600);
        var dst = new int[700];
        Array.Fill(dst, -1);
        list.CopyTo(dst);
        Assert.Equal(Enumerable.Range(0, 600), dst.Take(600));
        Assert.All(dst.Skip(600), v => Assert.Equal(-1, v)); // spare destination untouched
    }

    [Fact]
    public void CopyTo_TooShort_Throws()
    {
        var list = Range(100);
        Assert.Throws<ArgumentException>(() => list.CopyTo(new int[99]));
    }

    [Fact]
    public void ToArray_Empty_ReturnsEmpty()
    {
        Assert.Empty(ChunkedImmutableList<int>.Empty.ToArray());
        Assert.Empty(ChunkedImmutableList<int>.Empty.Chunks.ToArrayViaSpans());
    }

    [Fact]
    public void Chunks_ReflectStructuralSharing_NotSpareBuilderCapacity()
    {
        // Grow via builder (tail chunk gets full capacity while building), publish (tail trimmed),
        // then confirm the spans see exactly the live elements — not the builder's spare slots.
        var builder = ChunkedImmutableList<int>.Empty.ToBuilder();
        builder.AddRange(Enumerable.Range(0, 517).ToArray());
        var list = builder.ToImmutable();

        int seen = 0;
        foreach (var span in list.Chunks)
        {
            seen += span.Length;
        }
        Assert.Equal(517, seen);
        Assert.Equal(517, list.Count);
    }
}

internal static class ChunkSpanTestExtensions
{
    /// <summary>Test helper: flatten a <c>Chunks</c> view to an array via the span path.</summary>
    public static int[] ToArrayViaSpans(this ChunkedImmutableList<int>.ChunkSpans chunks)
    {
        var list = new List<int>();
        foreach (var span in chunks)
        {
            for (int i = 0; i < span.Length; i++)
            {
                list.Add(span[i]);
            }
        }
        return [.. list];
    }
}
