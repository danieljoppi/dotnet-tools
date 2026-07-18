using System.Runtime.CompilerServices;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

public class ChunkedImmutableListTests
{
    private static readonly int Chunk = ChunkedImmutableList<int>.ChunkCapacity;

    [Fact]
    public void Empty_HasZeroCount()
    {
        Assert.Empty(ChunkedImmutableList<int>.Empty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void CreateRange_SmallSizes_RoundTrips(int size)
    {
        var source = Enumerable.Range(0, size).ToArray();
        var list = ChunkedImmutableList<int>.CreateRange(source);
        Assert.Equal(size, list.Count);
        Assert.Equal(source, list.ToArray());
    }

    [Fact]
    public void CreateRange_AcrossChunkBoundaries_RoundTrips()
    {
        foreach (int size in new[] { Chunk - 1, Chunk, Chunk + 1, 3 * Chunk, 3 * Chunk + 17 })
        {
            var source = Enumerable.Range(0, size).ToArray();
            var list = ChunkedImmutableList<int>.CreateRange(source);
            Assert.Equal(size, list.Count);
            Assert.Equal(source, list.ToArray());
            for (int i = 0; i < size; i += 97)
            {
                Assert.Equal(i, list[i]);
            }
        }
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var list = ChunkedImmutableList<int>.CreateRange([1, 2, 3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[3]);
    }

    [Fact]
    public void SetItem_DoesNotMutateOriginal()
    {
        int size = 2 * Chunk + 5;
        var original = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, size));
        var updated = original.SetItem(Chunk + 3, -42);

        Assert.Equal(Chunk + 3, original[Chunk + 3]);
        Assert.Equal(-42, updated[Chunk + 3]);
        Assert.Equal(original.Count, updated.Count);
        // Every other element is untouched in both versions.
        for (int i = 0; i < size; i++)
        {
            if (i != Chunk + 3)
            {
                Assert.Equal(i, updated[i]);
            }
        }
    }

    [Fact]
    public void Add_ReturnsNewListAndPreservesOriginal()
    {
        var original = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, Chunk));
        var appended = original.Add(999);

        Assert.Equal(Chunk, original.Count);
        Assert.Equal(Chunk + 1, appended.Count);
        Assert.Equal(999, appended[Chunk]);
    }

    [Fact]
    public void Builder_BatchEdit_LeavesSourceUnchanged()
    {
        int size = 4 * Chunk;
        var original = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, size));
        var builder = original.ToBuilder();
        for (int i = 0; i < size; i += Chunk / 2)
        {
            builder[i] = -i;
        }
        var updated = builder.ToImmutable();

        for (int i = 0; i < size; i++)
        {
            Assert.Equal(i, original[i]);
            Assert.Equal(i % (Chunk / 2) == 0 ? -i : i, updated[i]);
        }
    }

    [Fact]
    public void Builder_ReusedAfterToImmutable_DoesNotCorruptPublishedList()
    {
        var original = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, Chunk));
        var builder = original.ToBuilder();
        builder[0] = 100;
        var first = builder.ToImmutable();

        builder[0] = 200; // must clone again, not write through into `first`
        builder.Add(300);
        var second = builder.ToImmutable();

        Assert.Equal(100, first[0]);
        Assert.Equal(Chunk, first.Count);
        Assert.Equal(200, second[0]);
        Assert.Equal(300, second[Chunk]);
    }

    [Fact]
    public void Builder_RemoveLast_ShrinksAcrossChunkBoundary()
    {
        var builder = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, Chunk + 2)).ToBuilder();
        builder.RemoveLast();
        builder.RemoveLast();
        builder.RemoveLast(); // crosses back into the first chunk
        var list = builder.ToImmutable();

        Assert.Equal(Chunk - 1, list.Count);
        Assert.Equal(Enumerable.Range(0, Chunk - 1), list.ToArray());
    }

    [Fact]
    public void Builder_RemoveLast_OnEmpty_Throws()
    {
        var builder = ChunkedImmutableList<int>.Empty.ToBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.RemoveLast());
    }

    [Fact]
    public void Builder_AddAfterRemoveToEmpty_Works()
    {
        var builder = ChunkedImmutableList<int>.CreateRange([1]).ToBuilder();
        builder.RemoveLast();
        builder.Add(7);
        var list = builder.ToImmutable();
        Assert.Equal([7], list.ToArray());
    }

    [Fact]
    public void ChunkArrays_StayBelowLohThreshold()
    {
        // The whole point of chunking: no backing array may reach the 85,000 byte LOH threshold.
        AssertChunkIsLohSafe<byte>();
        AssertChunkIsLohSafe<int>();
        AssertChunkIsLohSafe<long>();
        AssertChunkIsLohSafe<object>();
        AssertChunkIsLohSafe<Guid>();
        AssertChunkIsLohSafe<KeyValuePair<long, object>>();
        AssertChunkIsLohSafe<(long, long, long, long, long, long, long, long)>(); // 64-byte struct
    }

    private static void AssertChunkIsLohSafe<T>()
    {
        long chunkBytes = (long)ChunkedImmutableList<T>.ChunkCapacity * Unsafe.SizeOf<T>();
        Assert.True(chunkBytes < 85_000,
            $"{typeof(T).Name}: chunk of {ChunkedImmutableList<T>.ChunkCapacity} elements = {chunkBytes} bytes");
        Assert.True(ChunkedImmutableList<T>.ChunkCapacity >= 1);
    }

    [Fact]
    public void LargeList_MillionElements_IndexesCorrectly()
    {
        var list = ChunkedImmutableList<long>.CreateRange(Enumerable.Range(0, 1_000_000).Select(i => (long)i));
        Assert.Equal(1_000_000, list.Count);
        Assert.Equal(123_456L, list[123_456]);
        Assert.Equal(999_999L, list[999_999]);
    }

    [Fact]
    public void RandomizedFuzz_MatchesListModel()
    {
        var rng = new Random(12345);
        var model = new List<int>();
        var list = ChunkedImmutableList<int>.Empty;

        for (int round = 0; round < 200; round++)
        {
            var builder = list.ToBuilder();
            int ops = rng.Next(1, 200);
            for (int i = 0; i < ops; i++)
            {
                switch (rng.Next(3))
                {
                    case 0:
                        int value = rng.Next();
                        builder.Add(value);
                        model.Add(value);
                        break;
                    case 1 when model.Count > 0:
                        int index = rng.Next(model.Count);
                        int newValue = rng.Next();
                        builder[index] = newValue;
                        model[index] = newValue;
                        break;
                    case 2 when model.Count > 0:
                        builder.RemoveLast();
                        model.RemoveAt(model.Count - 1);
                        break;
                }
            }
            list = builder.ToImmutable();
            Assert.Equal(model.Count, list.Count);
        }
        Assert.Equal(model, list.ToArray());
    }
}
