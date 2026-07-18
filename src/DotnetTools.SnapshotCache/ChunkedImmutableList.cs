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
public sealed class ChunkedImmutableList<T> : IReadOnlyList<T>
{
    // Spine geometry: 1024 chunk references per spine block = 8 KB per block on 64-bit.
    private const int SpineBlockShift = 10;
    internal const int SpineBlockLength = 1 << SpineBlockShift;
    private const int SpineBlockMask = SpineBlockLength - 1;
    private const int SpineBlockOwnershipWords = SpineBlockLength / 64;

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

    /// <summary>An empty list whose chunks target <paramref name="targetChunkBytes"/> of payload
    /// (clamped to the LOH-safe maximum).</summary>
    internal static ChunkedImmutableList<T> EmptyWithTargetBytes(int targetChunkBytes)
    {
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

    /// <summary>Builds a list from a sequence. Allocates only chunk/spine-block sized arrays.</summary>
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

    public Enumerator GetEnumerator() => new(this);

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

    /// <summary>
    /// A mutable builder with copy-on-write at both granularities: a spine block is cloned the
    /// first time any chunk under it changes, and a chunk is cloned the first time it is written.
    /// Ownership is tracked in small bitsets, so builder bookkeeping itself never touches the LOH.
    /// Use one builder per update batch, then call <see cref="ToImmutable"/> and publish the result.
    /// </summary>
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
                block[s] = (T[])block[s].Clone();
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
                _blocks[b] = new T[SpineBlockLength][];
                _blockOwned[b >> 6] |= 1UL << b;
                _chunkOwned[b] = new ulong[SpineBlockOwnershipWords];
            }
            else
            {
                EnsureBlockOwned(b);
            }
            _blocks[b][s] = new T[1 << _shift];
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
    }
}
