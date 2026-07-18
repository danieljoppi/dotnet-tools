using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotnetTools.SnapshotCache;

/// <summary>
/// A compact open-addressing hash map from key to row index, used as one shard of
/// <see cref="SnapshotTable{TKey,TValue}"/>'s index. Compared to <c>Dictionary&lt;TKey,int&gt;</c>
/// (~39 B/entry effective) this stores ~13 B/slot for 8-byte keys (~22 B/entry at working load),
/// cutting the dominant share of a large table's footprint, and it clones as three array copies.
///
/// Layout: parallel arrays (keys / values / control byte per slot), exact (non-pow2) capacity
/// with multiply-shift slot mapping, linear probing with explicit wrap, tombstones on remove,
/// rehash (which purges tombstones) when occupancy passes 75%.
/// Not thread-safe for writes; published shards are immutable by convention (writers clone first),
/// which is what makes lock-free snapshot reads safe.
/// </summary>
internal sealed class ShardMap<TKey>
    where TKey : notnull
{
    private const byte Empty = 0;
    private const byte Full = 1;
    private const byte Tombstone = 2;
    private const int MinCapacity = 8;

    private readonly IEqualityComparer<TKey> _comparer;
    private TKey[] _keys;
    private int[] _values;
    private byte[] _states;
    private int _count;    // live entries
    private int _occupied; // live + tombstones

    internal ShardMap(IEqualityComparer<TKey> comparer, int capacity = MinCapacity)
    {
        _comparer = comparer;
        // Capacity is NOT rounded to a power of two: the start slot comes from a multiply-shift
        // onto [0, capacity) and probing wraps explicitly, so any size works. This matters: with
        // pow2 rounding a shard expecting ~190 entries lands on 512 slots (37% fill); exact sizing
        // keeps working fill near 66%, which is most of the index's memory footprint.
        capacity = Math.Max(MinCapacity, capacity);
        _keys = new TKey[capacity];
        _values = new int[capacity];
        _states = new byte[capacity];
    }

    private ShardMap(ShardMap<TKey> source)
    {
        _comparer = source._comparer;
        _keys = (TKey[])source._keys.Clone();
        _values = (int[])source._values.Clone();
        _states = (byte[])source._states.Clone();
        _count = source._count;
        _occupied = source._occupied;
    }

    public int Count => _count;

    public ShardMap<TKey> Clone() => new(this);

    // SnapshotTable routes keys to shards using the HIGH bits of a Fibonacci-mixed hash, so every
    // key in this shard shares those bits. Mix with a different odd constant here and take high
    // bits of that, so probing quality is independent of the shard routing.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int StartSlot(TKey key, int capacity)
    {
        uint mixed = (uint)(_comparer.GetHashCode(key) * -1937831252 /* 0x8C979A2C, distinct odd mix */);
        return (int)((mixed * (ulong)capacity) >> 32); // high-bits multiply-shift onto [0, capacity)
    }

    public bool TryGetValue(TKey key, out int value)
    {
        var states = _states;
        int capacity = states.Length;
        int slot = StartSlot(key, capacity);
        while (true)
        {
            byte state = states[slot];
            if (state == Empty)
            {
                value = 0;
                return false;
            }
            if (state == Full && _comparer.Equals(_keys[slot], key))
            {
                value = _values[slot];
                return true;
            }
            if (++slot == capacity)
            {
                slot = 0;
            }
        }
    }

    /// <summary>Adds the key or overwrites its value.</summary>
    public void Set(TKey key, int value)
    {
        if (_occupied * 4 >= _states.Length * 3)
        {
            Rehash();
        }
        int capacity = _states.Length;
        int slot = StartSlot(key, capacity);
        int firstTombstone = -1;
        while (true)
        {
            byte state = _states[slot];
            if (state == Empty)
            {
                int target = firstTombstone >= 0 ? firstTombstone : slot;
                _keys[target] = key;
                _values[target] = value;
                _states[target] = Full;
                _count++;
                if (target == slot)
                {
                    _occupied++;
                }
                return;
            }
            if (state == Tombstone)
            {
                if (firstTombstone < 0)
                {
                    firstTombstone = slot;
                }
            }
            else if (_comparer.Equals(_keys[slot], key))
            {
                _values[slot] = value;
                return;
            }
            if (++slot == capacity)
            {
                slot = 0;
            }
        }
    }

    public bool Remove(TKey key)
    {
        int capacity = _states.Length;
        int slot = StartSlot(key, capacity);
        while (true)
        {
            byte state = _states[slot];
            if (state == Empty)
            {
                return false;
            }
            if (state == Full && _comparer.Equals(_keys[slot], key))
            {
                _states[slot] = Tombstone;
                if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                {
                    _keys[slot] = default!;
                }
                _count--;
                return true;
            }
            if (++slot == capacity)
            {
                slot = 0;
            }
        }
    }

    private void Rehash()
    {
        // Size so live entries sit at ~55% load, which also purges all tombstones.
        int newCapacity = Math.Max(MinCapacity, _count * 9 / 5);
        var oldKeys = _keys;
        var oldValues = _values;
        var oldStates = _states;
        _keys = new TKey[newCapacity];
        _values = new int[newCapacity];
        _states = new byte[newCapacity];
        _count = 0;
        _occupied = 0;
        for (int i = 0; i < oldStates.Length; i++)
        {
            if (oldStates[i] == Full)
            {
                Set(oldKeys[i], oldValues[i]);
            }
        }
    }
}
