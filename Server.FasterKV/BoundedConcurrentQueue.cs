/*
Portions of the code in this file is subject to:

Copyright (c) 2020 Erik Rigtorp <erik@rigtorp.se>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
 */

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hearty.Server.FasterKV;

/// <summary>
/// Wraps <see cref="UInt32" /> to force it to occupy one
/// cache-line's worth of memory (assumed to be 64 bytes)
/// to avoid false sharing.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64, Pack = 8)]
internal struct CacheLinePaddedUint32
{
    [FieldOffset(60)]
    public uint Value;
}

/// <summary>
/// A multi-producer, multi-consumer queue of fixed capacity,
/// implemented with lock-free techniques.
/// </summary>
/// <typeparam name="T">
/// The item to be stored in the queue.
/// </typeparam>
/// <remarks>
/// This queue implements essentially the same algorithm from
/// <a href="https://github.com/rigtorp/MPMCQueue">
/// Erik Rigtorp's bounded multi-producer multi-consumer concurrent queue written in C++11
/// </a>, translated into C#.  Two modifications are made for
/// efficiency: 
/// <list type="bullet">
/// <item>
/// <para>
/// The original separates each item by
/// (at least) an assumed the hardware cache-line size to
/// prevent falsh sharing, while this implementation permutes
/// the index on the circular buffer so that adjacent accesses are 
/// located far away enough from each other in physical memory.  
/// </para>
/// <para>
/// Often <typeparamref name="T" /> is small in size, like a reference, 
/// and so not adding padding saves memory too.  It is assumed that
/// the capacity of the circular buffer is reasonably larger than the
/// expected number of concurrent threads accessing it.
/// </para>
/// <para>
/// Note also .NET does not allow controlling alignment in GC memory.
/// </para>
/// </item>
/// <item>
/// The capacity in this implementation is limited to powers of 2
/// to speed up taking the modulus of the head or tail ticket
/// to get the index into the circular buffer.
/// </item>
/// </list>
/// </remarks>
internal struct BoundedConcurrentQueue<T>
{
    /// <summary>
    /// Prepare an initially empty queue.
    /// </summary>
    /// <param name="capacity">
    /// Number of items that the queue may hold.  This number must be positive.
    /// and not greater than 2^30.  The actual capacity will be rounded
    /// up to the nearest power of 2.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacity" /> is invalid.
    /// </exception>
    public BoundedConcurrentQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity of the queue cannot be negative. ");

        if (capacity > (1 << 30))
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity of the queue cannot be greater than 2^30. ");

        // Round up the capacity to the nearest power of 2.
        // Also require that there be at least (about) 4 cache-lines'
        // worth of capacity to mitigate false sharing.
        int minCapacity = Math.Max(256 / Unsafe.SizeOf<T>(), 2);
        _log2Capacity = BitOperations.Log2((uint)Math.Max(capacity, minCapacity) - 1) + 1;

        capacity = 1 << _log2Capacity;

        _capacityMask = (uint)capacity - 1;

        _slots = new Slot[capacity];

        _head = default;
        _tail = default;
    }

    private struct Slot
    {
        public T Data;
        public uint Turn;
    }

    private readonly int _log2Capacity;
    private readonly uint _capacityMask;
    private readonly Slot[] _slots;

    private CacheLinePaddedUint32 _head;    
    private CacheLinePaddedUint32 _tail;

    /// <summary>
    /// The capacity of the queue, which may be rounded up from
    /// what was requested.
    /// </summary>
    public int Capacity => (int)_capacityMask + 1;

    /// <summary>
    /// Get the slot (in the circular buffer) that the head or tail ticket refers to.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Slot GetSlot(uint ticket)
    {
        // It is a well known fact the function v ⇒ k·v mod 2^n is a
        // bijection over Z/2^nZ when k is relatively prime to 2^n.  It looks
        // like multiplicative hashing, but for our purposes we do not need
        // to "scramble" the input sequence.  We only need the transformation
        // of neighboring points, to have a distance that exceeds the cache-line
        // size.  There is no performance gain in making that distance bigger.
        //
        // So, assuming each Slot is at least 8 bytes in length, setting k=11
        // is sufficient to ensure a separation of at least 8*k = 88 bytes.
        uint index = unchecked(ticket * 11u) & _capacityMask;

        // Skip the normal bounds check on array access
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_slots), index);
    }

    /// <summary>
    /// Get the current number of the cycle (how many turns the circular buffer
    /// has been revolved around) implied by the head or tail ticket,
    /// multiplied by two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetTurn(uint ticket) => unchecked(2 * (ticket >> _log2Capacity));

    /// <summary>
    /// Push an item onto the end of the queue, unless the queue
    /// is full.
    /// </summary>
    /// <param name="item">
    /// The item to put into the queue.
    /// </param>
    /// <returns>
    /// True if the item has been successfully pushed; false
    /// if the queue is already full.
    /// </returns>
    public bool TryEnqueue(T item)
    {
        unchecked
        {
            uint tail = Volatile.Read(ref _tail.Value);
            while (true)
            {
                uint prevTail = tail;
                ref Slot slot = ref GetSlot(prevTail);

                // The slot is unoccupied
                uint turn = GetTurn(prevTail);
                if (Volatile.Read(ref slot.Turn) == turn)
                {
                    // Must reserve the slot before storing the item
                    tail = Interlocked.CompareExchange(ref _tail.Value, prevTail + 1, prevTail);
                    if (tail == prevTail)
                    {
                        slot.Data = item;
                        Volatile.Write(ref slot.Turn, turn | 1);
                        return true;
                    }
                }
                else
                {
                    // Retry if one turn has advanced concurrently, otherwise quit
                    tail = Volatile.Read(ref _tail.Value);
                    if (tail == prevTail)
                        return false;
                }
            }
        }
    }

    /// <summary>
    /// Take an item from the front of the queue, unless the queue
    /// is empty.
    /// </summary>
    /// <param name="item">
    /// Set to the item retrieved from the queue, or the default
    /// value if the queue is empty.
    /// </param>
    /// <returns>
    /// True if an item has been successfully taken; false
    /// if the queue is currently empty.
    /// </returns>
    public bool TryDequeue(out T item)
    {
        unchecked
        {
            uint head = Volatile.Read(ref _head.Value);
            while (true)
            {
                uint prevHead = head;
                ref Slot slot = ref GetSlot(prevHead);

                // The slot has an item inside
                uint turn = GetTurn(prevHead) | 1;
                if (Volatile.Read(ref slot.Turn) == turn)
                {
                    // Must reserve the slot before consuming the item
                    head = Interlocked.CompareExchange(ref _head.Value, prevHead + 1, prevHead);
                    if (head == prevHead)
                    {
                        item = slot.Data;
                        slot.Data = default!;
                        Volatile.Write(ref slot.Turn, turn + 1);
                        return true;
                    }
                }
                else
                {
                    // Retry if one turn has advanced concurrently, otherwise quit
                    head = Volatile.Read(ref _head.Value);
                    if (head == prevHead)
                    {
                        item = default!;
                        return false;
                    }
                }
            }
        }
    }
}
