using System.Collections;
using System.Runtime.CompilerServices;

namespace DotnetTools.SnapshotCache;

/// <summary>
/// An immutable (persistent) list that stores its elements in fixed-size chunks instead of one
/// contiguous array.
///
/// Why chunks? Two reasons that matter for large in-memory caches:
/// <list type="bullet">
///   <item>
///     <b>No Large Object Heap allocations.</b> Every chunk is sized to stay under the 85,000-byte
///     LOH threshold, so a list with millions of elements never allocates a large array. (The spine —
///     the array of chunk references — stays under the threshold up to roughly 10 million elements
///     per 8-byte element; see <see cref="ChunkedImmutableList{T}.Builder"/> remarks.)
///   </item>
///   <item>
///     <b>Cheap updates via structural sharing.</b> Replacing one element copies only the spine and
///     the single affected chunk (O(chunk) instead of O(n) for <c>ImmutableArray.SetItem</c>), and a
///     batch update through <see cref="ToBuilder"/> copies each touched chunk at most once.
///     Untouched chunks are shared between the old and new list.
///   </item>
/// </list>
/// Reads are two array indexings (no tree traversal, unlike <c>ImmutableList&lt;T&gt;</c>).
/// </summary>
public sealed class ChunkedImmutableList<T> : IReadOnlyList<T>
{
    // Chunk capacity is a per-T constant: the largest power of two whose backing array stays
    // safely below the 85,000-byte LOH threshold, capped at 8192 elements.
    internal static readonly int Shift = ComputeShift();
    internal static readonly int ChunkCapacity = 1 << Shift;
    internal static readonly int IndexMask = ChunkCapacity - 1;

    private const int LohSafeChunkBytes = 64 * 1024;

    private static int ComputeShift()
    {
        int elementSize = Unsafe.SizeOf<T>();
        int shift = 0;
        while (shift < 13 && (1L << (shift + 1)) * elementSize <= LohSafeChunkBytes)
        {
            shift++;
        }
        return shift;
    }

    public static readonly ChunkedImmutableList<T> Empty = new([], 0);

    private readonly T[][] _chunks;
    private readonly int _count;

    private ChunkedImmutableList(T[][] chunks, int count)
    {
        _chunks = chunks;
        _count = count;
    }

    public int Count => _count;

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count)
            {
                ThrowIndexOutOfRange(index);
            }
            return _chunks[index >> Shift][index & IndexMask];
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "Index is out of range.");

    /// <summary>Builds a list from a sequence. Allocates only chunk-sized arrays.</summary>
    public static ChunkedImmutableList<T> CreateRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = Empty.ToBuilder();
        foreach (var item in items)
        {
            builder.Add(item);
        }
        return builder.ToImmutable();
    }

    /// <summary>Returns a new list with the element at <paramref name="index"/> replaced.
    /// Copies the spine and one chunk; all other chunks are shared with this list.</summary>
    public ChunkedImmutableList<T> SetItem(int index, T value)
    {
        if ((uint)index >= (uint)_count)
        {
            ThrowIndexOutOfRange(index);
        }
        var chunks = (T[][])_chunks.Clone();
        int c = index >> Shift;
        var chunk = (T[])chunks[c].Clone();
        chunk[index & IndexMask] = value;
        chunks[c] = chunk;
        return new ChunkedImmutableList<T>(chunks, _count);
    }

    /// <summary>Returns a new list with <paramref name="value"/> appended.</summary>
    public ChunkedImmutableList<T> Add(T value)
    {
        var builder = ToBuilder();
        builder.Add(value);
        return builder.ToImmutable();
    }

    /// <summary>Creates a mutable builder that shares all chunks with this list until they are written to.</summary>
    public Builder ToBuilder() => new(_chunks, _count);

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly T[][] _chunks;
        private readonly int _count;
        private int _index;
        private T[] _currentChunk;

        internal Enumerator(ChunkedImmutableList<T> list)
        {
            _chunks = list._chunks;
            _count = list._count;
            _index = -1;
            _currentChunk = [];
        }

        public T Current => _currentChunk[_index & IndexMask];

        object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            int next = _index + 1;
            if (next >= _count)
            {
                return false;
            }
            if ((next & IndexMask) == 0)
            {
                _currentChunk = _chunks[next >> Shift];
            }
            _index = next;
            return true;
        }

        public void Reset()
        {
            _index = -1;
            _currentChunk = [];
        }

        public readonly void Dispose()
        {
        }
    }

    /// <summary>
    /// A mutable builder over a <see cref="ChunkedImmutableList{T}"/> with chunk-level copy-on-write:
    /// each chunk is cloned at most once per builder session, the first time it is written to.
    /// Use one builder per update batch, then call <see cref="ToImmutable"/> and publish the result.
    /// </summary>
    public sealed class Builder
    {
        private T[][] _chunks;
        private bool[] _owned;      // _owned[i] == true → _chunks[i] is private to this builder
        private int _chunkCount;    // number of live chunks (may be < _chunks.Length spare capacity)
        private int _count;
        private bool _spineOwned;

        internal Builder(T[][] chunks, int count)
        {
            _chunks = chunks;
            _chunkCount = chunks.Length;
            _count = count;
            _owned = _chunkCount == 0 ? [] : new bool[_chunkCount];
            _spineOwned = false;
        }

        public int Count => _count;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                {
                    ThrowIndexOutOfRange(index);
                }
                return _chunks[index >> Shift][index & IndexMask];
            }
            set
            {
                if ((uint)index >= (uint)_count)
                {
                    ThrowIndexOutOfRange(index);
                }
                GetWritableChunk(index >> Shift)[index & IndexMask] = value;
            }
        }

        public void Add(T value)
        {
            int chunkIndex = _count >> Shift;
            if (chunkIndex == _chunkCount)
            {
                AppendChunk();
            }
            GetWritableChunk(chunkIndex)[_count & IndexMask] = value;
            _count++;
        }

        /// <summary>Removes the last element. Combined with a swap of the last element into the
        /// removed slot, this gives O(chunk) removal at any position (see <see cref="SnapshotTable{TKey,TValue}"/>).</summary>
        public void RemoveLast()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("The list is empty.");
            }
            _count--;
            int chunkIndex = _count >> Shift;
            if (_owned[chunkIndex] && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _chunks[chunkIndex][_count & IndexMask] = default!;
            }
            // Drop a chunk that became empty so the spine shrinks with the list.
            if ((_count & IndexMask) == 0 && chunkIndex < _chunkCount)
            {
                EnsureSpineOwned();
                _chunks[chunkIndex] = null!;
                _owned[chunkIndex] = false;
                _chunkCount = chunkIndex;
            }
        }

        public ChunkedImmutableList<T> ToImmutable()
        {
            if (_count == 0)
            {
                return Empty;
            }
            var spine = new T[_chunkCount][]; // exact-size copy of the live spine
            Array.Copy(_chunks, spine, _chunkCount);
            // Freeze: everything this builder hands out is now shared, so a later mutation
            // through this same builder must clone again.
            Array.Clear(_owned, 0, _chunkCount);
            _spineOwned = false;
            _chunks = spine;
            return new ChunkedImmutableList<T>(spine, _count);
        }

        private T[] GetWritableChunk(int chunkIndex)
        {
            var chunk = _chunks[chunkIndex];
            if (!_owned[chunkIndex])
            {
                EnsureSpineOwned();
                chunk = (T[])chunk.Clone();
                _chunks[chunkIndex] = chunk;
                _owned[chunkIndex] = true;
            }
            return chunk;
        }

        private void AppendChunk()
        {
            EnsureSpineOwned();
            if (_chunkCount == _chunks.Length)
            {
                int newCapacity = Math.Max(4, _chunks.Length * 2);
                Array.Resize(ref _chunks, newCapacity);
                Array.Resize(ref _owned, newCapacity);
            }
            _chunks[_chunkCount] = new T[ChunkCapacity];
            _owned[_chunkCount] = true;
            _chunkCount++;
        }

        private void EnsureSpineOwned()
        {
            if (!_spineOwned)
            {
                _chunks = (T[][])_chunks.Clone();
                if (_owned.Length < _chunks.Length)
                {
                    Array.Resize(ref _owned, _chunks.Length);
                }
                _spineOwned = true;
            }
        }
    }
}
