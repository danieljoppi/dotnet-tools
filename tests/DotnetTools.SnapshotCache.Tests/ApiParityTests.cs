using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>API-parity additions vs <c>ImmutableArray</c>/<c>ImmutableList</c> (issue #23):
/// <c>CreateRange(span)</c>, list-level <c>AddRange</c>, and <c>IndexOf</c>/<c>Contains</c>
/// (chunk-span scans with default-comparer semantics).</summary>
public class ApiParityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(513)]   // crosses a chunk boundary + trimmed tail
    [InlineData(4_097)]
    public void CreateRange_Span_MatchesEnumerable(int n)
    {
        var data = Enumerable.Range(0, n).ToArray();
        var fromSpan = ChunkedImmutableList<int>.CreateRange(data.AsSpan());
        Assert.Equal(data, fromSpan.ToArray());
        Assert.Equal(n, fromSpan.Count);
        if (n == 0)
        {
            Assert.Same(ChunkedImmutableList<int>.Empty, fromSpan);
        }
    }

    [Fact]
    public void AddRange_OnList_AppendsAndLeavesOriginalUnchanged()
    {
        var original = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, 500));

        var viaSpan = original.AddRange(Enumerable.Range(500, 100).ToArray().AsSpan());
        var viaEnumerable = original.AddRange(Enumerable.Range(500, 100));

        Assert.Equal(500, original.Count); // unchanged
        Assert.Equal(Enumerable.Range(0, 600), viaSpan.ToArray());
        Assert.Equal(Enumerable.Range(0, 600), viaEnumerable.ToArray());
        Assert.Same(original, original.AddRange(ReadOnlySpan<int>.Empty)); // empty is a no-op
        Assert.Throws<ArgumentNullException>(() => original.AddRange((IEnumerable<int>)null!));
    }

    [Fact]
    public void IndexOf_And_Contains_MatchListSemanticsAcrossChunks()
    {
        var list = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, 2_000));
        var model = Enumerable.Range(0, 2_000).ToList();

        foreach (int probe in new[] { 0, 1, 511, 512, 513, 1_999, -1, 2_000, 12_345 })
        {
            Assert.Equal(model.IndexOf(probe), list.IndexOf(probe));
            Assert.Equal(model.Contains(probe), list.Contains(probe));
        }
    }

    [Fact]
    public void IndexOf_ReturnsFirstOccurrence_WithDefaultComparer()
    {
        // Duplicates across a chunk boundary: IndexOf must return the first, like List<T>.
        var builder = ChunkedImmutableList<string>.Empty.ToBuilder();
        builder.AddRange(Enumerable.Range(0, 520).Select(i => i == 515 ? "target" : $"x{i}").ToArray());
        builder[10] = "target"; // earlier duplicate
        var list = builder.ToImmutable();

        Assert.Equal(10, list.IndexOf("target"));
        Assert.True(list.Contains("target"));
        Assert.Equal(-1, list.IndexOf("absent"));
    }

    [Fact]
    public void IndexOf_HandlesNullForReferenceTypes()
    {
        var list = ChunkedImmutableList<string?>.CreateRange(["a", null, "b"]);
        Assert.Equal(1, list.IndexOf(null));
        Assert.True(list.Contains(null));
    }
}
