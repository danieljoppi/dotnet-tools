using System.Collections;
using System.Runtime.CompilerServices;

namespace DotnetTools.SnapshotCache;

/// <summary>
/// An immutable (persistent) list that stores its elements in small fixed-size chunks reached
/// through a two-level spine, instead of one contiguous array.
///
/// Why this shape? Three properties that matter for very large in-memory caches:
/// <list type="bullet">
///   <item>
///     <b>No Large Object Heap allocations at any size.</b> Chunks default to ~4 KB of data,
///     spine blocks are 8 KB (1024 chunk references), and the top-level spine stays under the
///     85,000-byte LOH threshold all the way to <see cref="int.MaxValue"/> elements.
///   </item>
///   <item>
///     <b>Updates cost O(touched chunks), not O(n).</b> Replacing one element copies one chunk,
///     one spine block, and the top spine. A batch through <see cref="ToBuilder"/> copies each
///     touched chunk and spine block at most once; everything untouched is shared between the old
///     and new list (structural sharing), which is also what makes old snapshots cheap to hold.
///   </item>
///   <item>
///     <b>Array-speed reads.</b> An index read is three array indexings — no tree traversal,
///     unlike <c>ImmutableList&lt;T&gt;</c>.
///   </item>
/// </list>
/// The chunk size is tunable per instance via <see cref="EmptyWithChunkRows"/>: smaller chunks
/// make sparse random batches cheaper to copy (ideal for huge tables refreshed with relatively
/// small batches); larger chunks favor dense updates and sequential scans.
/// </summary>
[System.Diagnostics.DebuggerDisplay("Count = {Count}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(ChunkedImmutableList<>.DebugView))]
public sealed class ChunkedImmutableList<T> : IReadOnlyList<T>
{
    // Spine geometry: up to 1024 chunk references per spine block = 8 KB per block on 64-bit.
    // Blocks are allocated small and grown by doubling, so a list holding a handful of chunks
    // pays a few dozen bytes of spine, not 8 KB — important when many small lists coexist
    // (e.g. one bucket per shared key). Indexing is unaffected: a block's length is always
    // ≥ the number of chunks it holds, so reads never bounds-check against the block cap.
    private const int SpineBlockShift = 10;
    internal const int SpineBlockLength = 1 << SpineBlockShift;
    private const int SpineBlockMask = SpineBlockLength - 1;
    private const int SpineBlockOwnershipWords = SpineBlockLength / 64;
    private const int InitialSpineBlockLength = 4;

    // Default chunk payload target. 4 KB balances copy-on-write waste for sparse random batches
    // against per-array overhead; hard cap keeps any chunk safely below the 85,000-byte LOH bar.
    private const int DefaultTargetChunkBytes = 4 * 1024;
    private const int MaxChunkBytes = 64 * 1024;

    internal static int ShiftForTargetBytes(int targetChunkBytes)
    {
        int elementSize = Unsafe.SizeOf<T>();
        int shift = 0;
        while (shift < 13 && (1L << (shift + 1)) * elementSize <= targetChunkBytes)
        {
            shift++;
        }
        // Guard for very large structs: never let a single chunk reach the LOH.
        while (shift > 0 && (1L << shift) * elementSize > MaxChunkBytes)
        {
            shift--;
        }
        return shift;
    }

    internal static readonly int DefaultShift = ShiftForTargetBytes(DefaultTargetChunkBytes);
    internal static int DefaultChunkRows => 1 << DefaultShift;

    public static readonly ChunkedImmutableList<T> Empty = new([], 0, DefaultShift);

    /// <summary>
    /// An empty list whose chunks target <paramref name="targetChunkBytes"/> of payload, computed
    /// from <typeparamref name="T"/>'s size and clamped to the LOH-safe maximum. Prefer this over
    /// <see cref="EmptyWithChunkRows"/> when tuning copy-on-write granularity — smaller chunks make
    /// sparse random batches cheaper to copy, larger chunks favor dense updates and scans — since
    /// it spares callers the element-size arithmetic. Lists and builders derived from the result
    /// keep the chunk size.
    /// </summary>
    public static ChunkedImmutableList<T> EmptyWithTargetBytes(int targetChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetChunkBytes);
        int shift = ShiftForTargetBytes(targetChunkBytes);
        return shift == DefaultShift ? Empty : new ChunkedImmutableList<T>([], 0, shift);
    }

    /// <summary>An empty list whose chunks hold <paramref name="chunkRows"/> elements (a power of
    /// two). Lists and builders derived from it keep that chunk size.</summary>
    public static ChunkedImmutableList<T> EmptyWithChunkRows(int chunkRows)
    {
        if (chunkRows < 1 || (chunkRows & (chunkRows - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkRows), chunkRows, "Chunk rows must be a power of two.");
        }
        if ((long)chunkRows * Unsafe.SizeOf<T>() > MaxChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkRows), chunkRows,
                $"Chunk of {chunkRows} x {Unsafe.SizeOf<T>()}B elements would exceed {MaxChunkBytes} bytes.");
        }
        int shift = System.Numerics.BitOperations.Log2((uint)chunkRows);
        return shift == DefaultShift ? Empty : new ChunkedImmutableList<T>([], 0, shift);
    }

    private readonly T[][][] _blocks; // top spine → spine blocks (1024 chunk refs) → chunks
    private readonly int _count;
    private readonly int _shift;
    private readonly int _mask;

    private ChunkedImmutableList(T[][][] blocks, int count, int shift)
    {
        _blocks = blocks;
        _count = count;
        _shift = shift;
        _mask = (1 << shift) - 1;
    }

    public int Count => _count;

    /// <summary>Elements per chunk for this list instance.</summary>
    public int ChunkRows => 1 << _shift;

    internal T[][][] UnsafeBlocks => _blocks;

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count)
            {
                ThrowIndexOutOfRange(index);
            }
            int chunk = index >> _shift;
            return _blocks[chunk >> SpineBlockShift][chunk & SpineBlockMask][index & _mask];
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "Index is out of range.");

    /// <summary>Builds a list from a sequence. Allocates only chunk/spine-block sized arrays;
    /// arrays and lists are copied chunk-by-chunk via <see cref="Builder.AddRange(ReadOnlySpan{T})"/>.</summary>
    public static ChunkedImmutableList<T> CreateRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = Empty.ToBuilder();
        builder.AddRange(items);
        return builder.ToImmutable();
    }

    /// <summary>Builds a list from a span, copied chunk-by-chunk (no intermediate array).</summary>
    public static ChunkedImmutableList<T> CreateRange(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty)
        {
            return Empty;
        }
        var builder = Empty.ToBuilder();
        builder.AddRange(items);
        return builder.ToImmutable();
    }

    /// <summary>Builds a list from an array. An explicit overload so array arguments bind here
    /// (not ambiguously between the span and enumerable overloads) on every target framework.</summary>
    public static ChunkedImmutableList<T> CreateRange(T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return CreateRange(items.AsSpan());
    }

    /// <summary>Returns a new list with <paramref name="items"/> appended — a builder round-trip
    /// packaged as one call. Copies each touched chunk once; untouched structure is shared.</summary>
    public ChunkedImmutableList<T> AddRange(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty)
        {
            return this;
        }
        var builder = ToBuilder();
        builder.AddRange(items);
        return builder.ToImmutable();
    }

    /// <summary>Array overload of <see cref="AddRange(ReadOnlySpan{T})"/> — binds array arguments
    /// unambiguously across target frameworks.</summary>
    public ChunkedImmutableList<T> AddRange(T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return AddRange(items.AsSpan());
    }

    /// <inheritdoc cref="AddRange(ReadOnlySpan{T})"/>
    public ChunkedImmutableList<T> AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = ToBuilder();
        builder.AddRange(items);
        return builder.ToImmutable();
    }

    /// <summary>Index of the first element equal to <paramref name="value"/> under
    /// <see cref="EqualityComparer{T}.Default"/>, or -1. Scans chunk-by-chunk via
    /// <see cref="Chunks"/> — a span walk, not the per-element enumerator.</summary>
    public int IndexOf(T value)
    {
        var comparer = EqualityComparer<T>.Default;
        int baseIndex = 0;
        foreach (var span in Chunks)
        {
            for (int i = 0; i < span.Length; i++)
            {
                if (comparer.Equals(span[i], value))
                {
                    return baseIndex + i;
                }
            }
            baseIndex += span.Length;
        }
        return -1;
    }

    /// <summary>Whether any element equals <paramref name="value"/> under
    /// <see cref="EqualityComparer{T}.Default"/>.</summary>
    public bool Contains(T value) => IndexOf(value) >= 0;

    /// <summary>Returns a new list with the element at <paramref name="index"/> replaced.
    /// Copies the top spine, one spine block, and one chunk; everything else is shared.</summary>
    public ChunkedImmutableList<T> SetItem(int index, T value)
    {
        if ((uint)index >= (uint)_count)
        {
            ThrowIndexOutOfRange(index);
        }
        var blocks = (T[][][])_blocks.Clone();
        int chunkIndex = index >> _shift;
        int b = chunkIndex >> SpineBlockShift;
        var block = (T[][])blocks[b].Clone();
        var chunk = (T[])block[chunkIndex & SpineBlockMask].Clone();
        chunk[index & _mask] = value;
        block[chunkIndex & SpineBlockMask] = chunk;
        blocks[b] = block;
        return new ChunkedImmutableList<T>(blocks, _count, _shift);
    }

    /// <summary>Returns a new list with <paramref name="value"/> appended.</summary>
    public ChunkedImmutableList<T> Add(T value)
    {
        var builder = ToBuilder();
        builder.Add(value);
        return builder.ToImmutable();
    }

    /// <summary>Creates a mutable builder that shares all spine blocks and chunks with this list
    /// until they are written to.</summary>
    public Builder ToBuilder() => new(this);

    /// <summary>
    /// Enumerates the backing chunks as <see cref="ReadOnlySpan{T}"/> slices of live elements —
    /// <c>foreach (var span in list.Chunks) { ... }</c>. A full scan then runs as a handful of
    /// tight span loops (one per chunk) instead of a per-element enumerator, and consumers can
    /// vectorize each span. Each span exposes exactly the chunk's live elements (the tail is
    /// <see cref="Count"/>-bounded), never spare capacity. Spans are read-only views into the
    /// immutable backing arrays.
    /// </summary>
    public ChunkSpans Chunks => new(this);

    /// <summary>Copies every element into <paramref name="destination"/> as chunk-sized block
    /// copies. Throws if the span is shorter than <see cref="Count"/>.</summary>
    public void CopyTo(Span<T> destination)
    {
        if (destination.Length < _count)
        {
            throw new ArgumentException("Destination span is shorter than the list.", nameof(destination));
        }
        int offset = 0;
        foreach (var chunk in Chunks)
        {
            chunk.CopyTo(destination[offset..]);
            offset += chunk.Length;
        }
    }

    /// <summary>Materializes the list into a new array via chunk-sized copies — faster than the
    /// element-by-element LINQ <c>ToArray()</c>, which walks the enumerator one call per element.</summary>
    public T[] ToArray()
    {
        if (_count == 0)
        {
            return [];
        }
        var result = new T[_count];
        CopyTo(result);
        return result;
    }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>A <c>foreach</c>-able view over the backing chunks; see <see cref="Chunks"/>.</summary>
    public readonly struct ChunkSpans
    {
        private readonly ChunkedImmutableList<T> _list;

        internal ChunkSpans(ChunkedImmutableList<T> list) => _list = list;

        public ChunkSpanEnumerator GetEnumerator() => new(_list);
    }

    /// <summary>Yields each chunk's live elements as a <see cref="ReadOnlySpan{T}"/>.</summary>
    public struct ChunkSpanEnumerator
    {
        private readonly T[][][] _blocks;
        private readonly int _count;
        private readonly int _shift;
        private readonly int _chunkCount;
        private int _chunkIndex; // -1 before the first MoveNext
        private T[] _chunk;
        private int _liveLength;

        internal ChunkSpanEnumerator(ChunkedImmutableList<T> list)
        {
            _blocks = list._blocks;
            _count = list._count;
            _shift = list._shift;
            _chunkCount = _count == 0 ? 0 : ((_count - 1) >> _shift) + 1;
            _chunkIndex = -1;
            _chunk = [];
            _liveLength = 0;
        }

        public readonly ReadOnlySpan<T> Current => _chunk.AsSpan(0, _liveLength);

        public bool MoveNext()
        {
            int next = _chunkIndex + 1;
            if (next >= _chunkCount)
            {
                return false;
            }
            _chunk = _blocks[next >> SpineBlockShift][next & SpineBlockMask];
            // Bound by Count so the tail exposes only live elements even if a chunk ever carried
            // spare capacity (published tails are trimmed, so this is normally chunk.Length).
            _liveLength = Math.Min(_chunk.Length, _count - (next << _shift));
            _chunkIndex = next;
            return true;
        }
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly T[][][] _blocks;
        private readonly int _count;
        private readonly int _shift;
        private readonly int _mask;
        private int _index;
        private T[] _currentChunk;

        internal Enumerator(ChunkedImmutableList<T> list)
        {
            _blocks = list._blocks;
            _count = list._count;
            _shift = list._shift;
            _mask = list._mask;
            _index = -1;
            _currentChunk = [];
        }

        public readonly T Current => _currentChunk[_index & _mask];

        readonly object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            int next = _index + 1;
            if (next >= _count)
            {
                return false;
            }
            if ((next & _mask) == 0)
            {
                int chunk = next >> _shift;
                _currentChunk = _blocks[chunk >> SpineBlockShift][chunk & SpineBlockMask];
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

    /// <summary>Debugger proxy: shows elements instead of the raw <c>T[][][]</c> spine.</summary>
    internal sealed class DebugView
    {
        private readonly ChunkedImmutableList<T> _list;

        public DebugView(ChunkedImmutableList<T> list) => _list = list;

        [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.RootHidden)]
        public T[] Items => _list.ToArray();
    }

    /// <summary>
    /// A mutable builder with copy-on-write at both granularities: a spine block is cloned the
    /// first time any chunk under it changes, and a chunk is cloned the first time it is written.
    /// Ownership is tracked in small bitsets, so builder bookkeeping itself never touches the LOH.
    /// Use one builder per update batch, then call <see cref="ToImmutable"/> and publish the result.
    /// </summary>
    [System.Diagnostics.DebuggerDisplay("Count = {Count}")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(ChunkedImmutableList<>.Builder.DebugView))]
    public sealed class Builder
    {
        private readonly int _shift;
        private readonly int _mask;
        private T[][][] _blocks;        // private top spine copy; blocks/chunks shared until owned
        private ulong[] _blockOwned;    // bit per spine block
        private ulong[]?[] _chunkOwned; // per owned block: bit per chunk (16 ulongs)
        private int _chunkCount;
        private int _count;

        internal Builder(ChunkedImmutableList<T> list)
        {
            _shift = list._shift;
            _mask = list._mask;
            _blocks = (T[][][])list._blocks.Clone();
            _chunkCount = list._count == 0 ? 0 : ((list._count - 1) >> _shift) + 1;
            _count = list._count;
            _blockOwned = new ulong[WordsFor(_blocks.Length)];
            _chunkOwned = new ulong[]?[_blocks.Length];
        }

        public int Count => _count;

        public int ChunkRows => 1 << _shift;

        private static int WordsFor(int bits) => (bits + 63) >> 6;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                {
                    ThrowIndexOutOfRange(index);
                }
                int chunk = index >> _shift;
                return _blocks[chunk >> SpineBlockShift][chunk & SpineBlockMask][index & _mask];
            }
            set
            {
                if ((uint)index >= (uint)_count)
                {
                    ThrowIndexOutOfRange(index);
                }
                GetWritableChunk(index >> _shift)[index & _mask] = value;
            }
        }

        public void Add(T value)
        {
            int chunkIndex = _count >> _shift;
            if (chunkIndex == _chunkCount)
            {
                AppendChunk();
            }
            GetWritableChunk(chunkIndex)[_count & _mask] = value;
            _count++;
        }

        /// <summary>Appends a run of elements, copying chunk-sized spans instead of paying the
        /// per-element chunk resolution of <see cref="Add"/>.</summary>
        public void AddRange(ReadOnlySpan<T> items)
        {
            int offset = 0;
            while (offset < items.Length)
            {
                int chunkIndex = _count >> _shift;
                if (chunkIndex == _chunkCount)
                {
                    AppendChunk();
                }
                var chunk = GetWritableChunk(chunkIndex);
                int slot = _count & _mask;
                int copy = Math.Min(items.Length - offset, chunk.Length - slot);
                items.Slice(offset, copy).CopyTo(chunk.AsSpan(slot));
                _count += copy;
                offset += copy;
            }
        }

        /// <summary>Appends an array via the bulk span path. This exact-match overload exists for
        /// callers on C# 12 (the .NET 8 default), where an array argument is otherwise ambiguous
        /// between the span and enumerable overloads (CS0121) — C# 14's first-class span
        /// conversions resolve it, but the library must compile from LTS toolchains too.</summary>
        public void AddRange(T[] items)
        {
            ArgumentNullException.ThrowIfNull(items);
            AddRange(items.AsSpan());
        }

        /// <summary>Appends a sequence; arrays and lists take the bulk span path.</summary>
        public void AddRange(IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            switch (items)
            {
                case T[] array:
                    AddRange(array.AsSpan());
                    break;
                case List<T> list:
                    AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
                    break;
                default:
                    foreach (var item in items)
                    {
                        Add(item);
                    }
                    break;
            }
        }

        /// <summary>Removes the last element. Combined with a swap of the last element into the
        /// removed slot this gives O(chunk) removal at any position (see <see cref="SnapshotTable{TKey,TValue}"/>).</summary>
        public void RemoveLast()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("The list is empty.");
            }
            _count--;
            int chunkIndex = _count >> _shift;
            int b = chunkIndex >> SpineBlockShift;
            int s = chunkIndex & SpineBlockMask;
            if (IsChunkOwned(b, s) && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _blocks[b][s][_count & _mask] = default!;
            }
            // Drop a chunk that became empty so the spine shrinks with the list.
            if ((_count & _mask) == 0)
            {
                EnsureBlockOwned(b);
                _blocks[b][s] = null!;
                _chunkOwned[b]![s >> 6] &= ~(1UL << s);
                _chunkCount = chunkIndex;
            }
        }

        public ChunkedImmutableList<T> ToImmutable()
        {
            if (_count == 0)
            {
                return _shift == DefaultShift ? Empty : new ChunkedImmutableList<T>([], 0, _shift);
            }
            // Trim a partially filled tail chunk to its exact element count, so a list of 10
            // elements retains a 10-slot array, not a full chunk. Reads are unaffected (indexes
            // into the tail are bounded by Count); the builder re-expands the tail to full chunk
            // size on the next append.
            int tail = _count & _mask;
            if (tail != 0)
            {
                int lastChunk = _chunkCount - 1;
                int b = lastChunk >> SpineBlockShift;
                int s = lastChunk & SpineBlockMask;
                var chunk = _blocks[b][s];
                if (chunk.Length != tail)
                {
                    var trimmed = AllocateChunk(tail);
                    Array.Copy(chunk, trimmed, tail);
                    EnsureBlockOwned(b);
                    _blocks[b][s] = trimmed;
                }
            }
            int blockCount = ((_chunkCount - 1) >> SpineBlockShift) + 1;
            var top = new T[blockCount][][];
            Array.Copy(_blocks, top, blockCount);
            // Freeze: everything reachable from the published list is now shared, so any later
            // mutation through this same builder must clone again.
            Array.Clear(_blockOwned);
            Array.Clear(_chunkOwned);
            return new ChunkedImmutableList<T>(top, _count, _shift);
        }

        private bool IsChunkOwned(int block, int slot)
        {
            var words = _chunkOwned[block];
            return words is not null && (words[slot >> 6] & (1UL << slot)) != 0;
        }

        private T[] GetWritableChunk(int chunkIndex)
        {
            int b = chunkIndex >> SpineBlockShift;
            int s = chunkIndex & SpineBlockMask;
            EnsureBlockOwned(b);
            var block = _blocks[b];
            var owned = _chunkOwned[b]!;
            if ((owned[s >> 6] & (1UL << s)) == 0)
            {
                // Clone at full chunk size: the shared source may be a trimmed tail chunk
                // (published by ToImmutable), and an owned chunk must accept appends.
                var source = block[s];
                var copy = AllocateChunk(1 << _shift);
                Array.Copy(source, copy, source.Length);
                block[s] = copy;
                owned[s >> 6] |= 1UL << s;
            }
            return block[s];
        }

        private void AppendChunk()
        {
            int b = _chunkCount >> SpineBlockShift;
            int s = _chunkCount & SpineBlockMask;
            if (b == _blocks.Length)
            {
                int newLength = Math.Max(4, _blocks.Length * 2);
                Array.Resize(ref _blocks, newLength);
                Array.Resize(ref _blockOwned, WordsFor(newLength));
                Array.Resize(ref _chunkOwned, newLength);
            }
            if (s == 0 || _blocks[b] is null)
            {
                _blocks[b] = new T[InitialSpineBlockLength][];
                _blockOwned[b >> 6] |= 1UL << b;
                _chunkOwned[b] = new ulong[SpineBlockOwnershipWords];
            }
            else
            {
                EnsureBlockOwned(b);
                if (s == _blocks[b].Length)
                {
                    Array.Resize(ref _blocks[b], Math.Min(SpineBlockLength, _blocks[b].Length * 2));
                }
            }
            _blocks[b][s] = AllocateChunk(1 << _shift);
            _chunkOwned[b]![s >> 6] |= 1UL << s;
            _chunkCount++;
        }

        private void EnsureBlockOwned(int b)
        {
            if ((_blockOwned[b >> 6] & (1UL << b)) == 0)
            {
                _blocks[b] = (T[][])_blocks[b].Clone();
                _blockOwned[b >> 6] |= 1UL << b;
                _chunkOwned[b] = new ulong[SpineBlockOwnershipWords];
            }
        }

        // Allocate a chunk array, skipping the CLR's zero-fill for reference-free element types.
        // Safe because: on a clone/trim the copied region is overwritten immediately, and the
        // tail slots of a not-yet-full chunk are always written by Add before they can be read
        // (reads are Count-bounded, and ToImmutable trims the tail to the exact live count, so no
        // uninitialized slot is ever published or observed). For element types that contain
        // references the array MUST be zeroed, or the GC would trace a garbage pointer — the
        // IsReferenceOrContainsReferences check is a JIT-time constant, so the branch folds away.
        private static T[] AllocateChunk(int length) =>
            RuntimeHelpers.IsReferenceOrContainsReferences<T>()
                ? new T[length]
                : GC.AllocateUninitializedArray<T>(length);

        /// <summary>Debugger proxy showing the builder's current elements.</summary>
        internal sealed class DebugView
        {
            private readonly Builder _builder;

            public DebugView(Builder builder) => _builder = builder;

            [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.RootHidden)]
            public T[] Items
            {
                get
                {
                    var items = new T[_builder._count];
                    for (int i = 0; i < items.Length; i++)
                    {
                        items[i] = _builder[i];
                    }
                    return items;
                }
            }
        }
    }
}
