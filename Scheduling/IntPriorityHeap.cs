using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace JobBank.Scheduling
{
    /// <summary>
    /// A priority queue specialized for integer keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This priority queue is implemented as a octonary (8-way) heap
    /// represented as an array.  Each group of 8 keys, being integers, 
    /// can be quickly compared in parallel using SIMD instructions
    /// on modern CPUs.  An octonary heap is faster than a binary heap
    /// because the former is shallower and reads from memory in a more
    /// sequential manner.
    /// </para>
    /// <para>
    /// This type is a structure only for performance reasons.
    /// It should not be default-initialized.
    /// </para>
    /// <para>
    /// This type is not thread-safe.  The user must lock if 
    /// there is any possibility it can be accessed simultaneously
    /// from multiple threads.
    /// </para>
    /// </remarks>
    /// <typeparam name="TValue">The type of value to store
    /// in the heap, associated to the integer priority key. </typeparam>
    internal struct IntPriorityHeap<TValue>
    {
        /// <summary>
        /// Called whenever indices must be updated in the heap.
        /// </summary>
        private readonly IndexUpdateCallback? _indexUpdateCallback;

        /// <summary>
        /// A function that is called whenever an element in the priority
        /// heap moves and is assigned a new index.
        /// </summary>
        /// <remarks>
        /// This feature allows priority keys of existing elements to be changed
        /// efficiently, given their indices. Generally, this feature can only work 
        /// if <see cref="TValue" /> is an object reference or (is a structure that)
        /// wraps an object reference.  
        /// </remarks>
        /// <param name="element">Reference to the element in the array. </param>
        /// <param name="index">The new index being assigned. 
        /// If the element is about to be removed, this index is -1. </param>
        public delegate void IndexUpdateCallback(ref TValue element, int index);

        /// <summary>
        /// Invokes <see cref="_indexUpdateCallback" /> if it is non-null.
        /// </summary>
        private void InvokeIndexUpdateCallback(ref TValue element, int index)
            => _indexUpdateCallback?.Invoke(ref element, index);

        /// <summary>
        /// The number of active (valid) elements in <see cref="_keys" />
        /// and <see cref="_values" />.
        /// </summary>
        private int _count;

        /// <summary>
        /// The keys of elements in the heap, listed in breadth-first order. 
        /// </summary>
        private int[] _keys;

        /// <summary>
        /// The values of elements corresponding to the keys in <see cref="_keys" />.
        /// </summary>
        private TValue[] _values;

        /// <summary>
        /// Prepare an initially empty heap.
        /// </summary>
        /// <param name="indexTracker">Called whenever indices
        /// of elements in the heap need to be updated. </param>
        /// <param name="capacity">The initial capacity 
        /// of the arrays allocates for the priority heap.
        /// </param>
        public IntPriorityHeap(IndexUpdateCallback? callback, int capacity = 73)
        {
            int newCapacity = RoundUpCapacity(capacity);

            _indexUpdateCallback = callback;
            _count = 0;

            var keys = newCapacity > 0 ? new int[newCapacity] : Array.Empty<int>();
            PadKeysAtEnd(keys, 0);

            _keys = keys;
            _values = newCapacity > 0 ? new TValue[newCapacity] : Array.Empty<TValue>();
        }

        /// <summary>
        /// Compare some keys of the d-ary heap against a target,
        /// and report some index of the key that differs, if any.
        /// </summary>
        /// <param name="target">The key to compare against. </param>
        /// <param name="keys">The array of keys. </param>
        /// <param name="startIndex">
        /// The index of the first element in the group of d keys
        /// from the priority heap, to compare against <paramref name="target"/>.
        /// It must be valid for the <paramref name="keys"/> array.
        /// </param>
        /// <returns>
        /// The index of an element in the group of d keys that compares
        /// greater than <paramref name="target"/>,
        /// numbered from 0 to d-1.
        /// The return value plus <paramref name="startIndex" />
        /// is the actual index of the key in the heap.  
        /// The return value is -1 if all the d keys are less than
        /// or equal than <paramref name="target" />.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CompareForKeysGreaterThan(int target, int[] keys, int startIndex)
        {
            if (Avx2.IsSupported)
            {
                // Check the index manually in case the data is inconsistent 
                // because of (abusive) struct tearing from the user, as the
                // check is not implied from the unsafe code below.
                if (startIndex + Ways > keys.Length)
                    throw new ArgumentOutOfRangeException(nameof(startIndex), "Index is not valid for the keys array. ");

                Vector256<int> comparands;
                unsafe
                {
                    fixed (int* p = keys)
                        comparands = Avx2.LoadVector256(p + startIndex);
                }

                var targets = Vector256.Create(target);
                var results = Avx2.CompareGreaterThan(comparands, targets);

                var mask = (uint)Avx2.MoveMask(results.AsByte());
                uint j = (32 - Lzcnt.LeadingZeroCount(mask)) / sizeof(int);
                return (int)j - 1;
            }
            else
            {
                for (int j = 0; j < Ways; ++j)
                {
                    if (keys[startIndex + j] > target)
                        return j;
                }

                return -1;
            }
        }

        /// <summary>
        /// Get the index of the first child of an element in a d-ary heap.
        /// </summary>
        /// <param name="index">The index i of the parent element in the
        /// array representation of the heap.  Must not be negative.
        /// </param>
        /// <remarks>
        /// Given by the formula: i∙d + 1
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetLeftmostChildIndex(int parentIndex)
            => (parentIndex << Log2OfWays) + 1;

        /// <summary>
        /// Get the index that the parent of an element in a d-ary heap lives at.
        /// </summary>
        /// <param name="index">The index i of the child element in the
        /// array representation of the heap.  Must be positive.
        /// </param>
        /// <remarks>
        /// Given by the formula: ⌊ (i-1)/d ⌋
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetParentIndex(int childIndex)
            => (childIndex - 1) >> Log2OfWays;

        /// <summary>
        /// Swap two entries in the heap and update the index tracker
        /// for the entry that ends up in the first slot.
        /// </summary>
        /// <param name="keyA">Reference to the slot of the key
        /// for the first entry. </param>
        /// <param name="keyA">Reference to the slot of the key
        /// for the second entry. </param>
        /// <param name="indexA">The index of the first entry. 
        /// This index will also be reported to <see cref="IArrayIndexTracker{TValue}.ChangeIndex"/>,
        /// for the value of the first entry after swapping.
        /// </param>
        /// <param name="indexB">The index of the second entry. 
        /// </param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SwapEntries(ref int keyA, ref int keyB, 
                                 int indexA, int indexB)
        {
            TValue[] values = _values;

            ref TValue valueA = ref values[indexA];
            ref TValue valueB = ref values[indexB];

            int tempKey = keyA;
            keyA = keyB;
            keyB = tempKey;

            TValue tempValue = valueA;
            valueA = valueB;
            valueB = tempValue;

            InvokeIndexUpdateCallback(ref valueA, indexA);
        }

        /// <summary>
        /// Restore the heap property, downwards to the lower layers.
        /// </summary>
        /// <param name="index">The index of the newly set 
        /// element at the root of the sub-tree that might have
        /// disturbed the heap property. </param>
        private void BubbleDown(int index)
            => BubbleDown(index, ref _keys[index]);

        /// <summary>
        /// Restore the heap property, downwards to the lower layers.
        /// </summary>
        /// <param name="index">The index of the newly set 
        /// element at the root of the sub-tree that might have
        /// disturbed the heap property. </param>
        /// <param name="key">Index of the key for the element
        /// at <paramref name="index"/>. 
        /// </param>
        private void BubbleDown(int index, ref int key)
        {
            int count = _count;
            int childrenIndex = GetLeftmostChildIndex(index);
            if (childrenIndex < count)
                return;

            int[] keys = _keys;

            do
            {
                int j = CompareForKeysGreaterThan(key, keys, childrenIndex);
                if (j < 0)
                    break;

                int childIndex = childrenIndex + j;
                ref int childKey = ref keys[childIndex];

                SwapEntries(ref key, ref childKey, index, childIndex);

                index = childIndex;
                key = ref childKey;

                childrenIndex = GetLeftmostChildIndex(index);
            } while (childrenIndex < count);

            InvokeIndexUpdateCallback(ref _values[index], index);
        }

        /// <summary>
        /// Restore the heap property, upwards to the upper layers.
        /// </summary>
        /// <param name="index">The index of the newly set 
        /// element at the leaf of a sub-tree that might have
        /// disturbed the heap property. </param>
        /// <param name="key">Index of the key for the element
        /// at <paramref name="index" />. 
        /// </param>
        private void BubbleUp(int index, ref int key)
        {
            if (index == 0)
                return;

            int[] keys = _keys;

            do
            {
                int parentIndex = GetParentIndex(index);

                ref int parentKey = ref keys[parentIndex];
                if (key <= parentKey)
                    break;

                SwapEntries(ref key, ref parentKey, index, parentIndex);

                index = parentIndex;
                key = ref parentKey;
            } while (index > 0);

            InvokeIndexUpdateCallback(ref _values[index], index);
        }

        /// <summary>
        /// Add an element to the priority heap and put it into
        /// a proper position.
        /// </summary>
        /// <param name="key">The priority key for the new element. </param>
        /// <param name="value">The value associated to the key. </param>
        public void Insert(int key, in TValue value)
        {
            int index = _count;
            int newCount = index + 1;

            if (PrepareArrays(newCount, preserve: true))
                PadKeysAtEnd(_keys, newCount);

            ref int keySlot = ref _keys[index];
            ref TValue valueSlot = ref _values[index];

            keySlot = key;
            valueSlot = value;

            _count = newCount;

            InvokeIndexUpdateCallback(ref valueSlot, index);

            BubbleUp(index, ref keySlot);
        }

        /// <summary>
        /// Extract the element with the maximum priority out
        /// of this priority heap.
        /// </summary>
        /// <returns>The element extracted, that was previously the maximum
        /// in the priority heap. </param>
        public KeyValuePair<int, TValue> TakeMaximum()
        {
            int[] keys = _keys;

            ref int keySlot = ref keys[0];
            ref TValue valueSlot = ref _values[0];

            InvokeIndexUpdateCallback(ref valueSlot, -1);

            var key = keySlot;
            var value = valueSlot;

            valueSlot = default!;

            int index = _count - 1;

            ref int lastKeySlot = ref keys[index];

            SwapEntries(ref keySlot, ref lastKeySlot, 0, index);
            --_count;

            lastKeySlot = int.MinValue;

            BubbleDown(0, ref keySlot);

            return new KeyValuePair<int, TValue>(key, value);
        }

        /// <summary>
        /// Get the element with the maximum priority without extracting it out
        /// of this priority heap.
        /// </summary>
        /// <returns>The element in the heap 
        /// with the currently maximum priority. </param>
        public KeyValuePair<int, TValue> PeekMaximum()
        {
            return new KeyValuePair<int, TValue>(_keys[0], _values[0]);
        }

        /// <summary>
        /// Whether this priority heap has any elements.
        /// </summary>
        public bool IsNonEmpty => _count > 0;

        /// <summary>
        /// Number of elements stored in this priority heap currently.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Get or set an existing element of this priority heap
        /// at the given index.
        /// </summary>
        /// <remarks>
        /// The heap property is restored if the key of the element changes.
        /// </remarks>
        /// <param name="index">The index of the element in the 
        /// priority heap. </param>
        public KeyValuePair<int, TValue> this[int index]
        {
            get
            {
                return new KeyValuePair<int, TValue>(_keys[index], _values[index]);
            }
            set
            {
                ref int keySlot = ref _keys[index];

                var oldKey = keySlot;
                var newKey = value.Key;
                
                keySlot = newKey;

                ref TValue valueSlot = ref _values[index];
                valueSlot = value.Value;

                InvokeIndexUpdateCallback(ref valueSlot, index);

                if (newKey > oldKey)
                    BubbleUp(index, ref keySlot);
                else if (newKey < oldKey)
                    BubbleDown(index, ref keySlot);
            }
        }

        /// <summary>
        /// A user-specified function called by <see cref="Initialize" /> 
        /// to fill in the heap with the desired entries.
        /// </summary>
        /// <typeparam name="TState">Type of the arbitrary state.
        /// </typeparam>
        /// <param name="state">An arbitrary state that can be passed
        /// to the function, to avoid many allocations for this delegate.
        /// </param>
        /// <param name="keys">The keys that this function should populate.
        /// This span will have the length requested in <see cref="Initialize" />.
        /// </param>
        /// <param name="values">The values associated to the keys at the
        /// same indices.  Has the same length as <paramref name="keys" />.
        /// </param>
        /// <returns>The final count of the number of entries in the heap;
        /// this function may fill in less than the initially requested
        /// capacity in the heap.
        /// </returns>
        public delegate int PopulateEntriesFunction<TState>(ref TState state, 
                                                            in Span<int> keys, 
                                                            in Span<TValue> values);
        
        /// <summary>
        /// Ensure that the arrays representing the heap are big enough
        /// </summary>
        /// <param name="count">The desired maximum number of elements in the heap
        /// to allocate for. </param>
        /// <param name="preserve">Whether to preserve existing elements in the heap,
        /// if the arrays must be re-allocated.
        /// </param>
        /// <returns>
        /// Whether the arrays were re-allocated.
        /// </returns>
        private bool PrepareArrays(int count, bool preserve)
        {
            int oldCapacity = _keys.Length;
            if (count > oldCapacity)
            {
                int newCapacity = RoundUpCapacity(count);

                int[] newKeys = new int[newCapacity];
                TValue[] newValues = new TValue[newCapacity];

                if (preserve)
                {
                    int oldCount = _count;
                    _keys.AsSpan()[0..oldCount].CopyTo(newKeys);
                    _values.AsSpan()[0..oldCount].CopyTo(newValues);
                }

                _keys = newKeys;
                _values = newValues;

                return true;
            }

            return false;
        }

        /// <summary>
        /// The logarithm to base 2 of <see cref="Ways" />.
        /// </summary>
        private const int Log2OfWays = 3;

        /// <summary>
        /// Number of ways d (branching factor) of this d-ary heap,
        /// assumed to be a power of two, and is at least 2.
        /// </summary>
        private const int Ways = 1 << Log2OfWays;

        /// <summary>
        /// <see cref="Ways"/> minus one, used for masking bits.
        /// </summary>
        private const uint WaysMinusOne = (uint)(Ways - 1);

        /// <summary>
        /// Get the layer of a d-ary heap that an element lives at.
        /// </summary>
        /// <param name="index">The index of the element in the
        /// array representation of the heap.  Must not be negative.
        /// </param>
        /// <remarks>
        /// Given by the formula: ⌊ log_d ((d-1)i+1) ⌋
        /// </remarks>
        /// <returns>
        /// The layer of the heap, numbered as 0, 1, 2, ...
        /// with 0 being the root layer.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetLayerFromIndex(int index)
        {
            var c = (uint)index * WaysMinusOne + 1;
            var level = BitOperations.Log2(c) >> Log2OfWays;
            return level;
        }

        /// <summary>
        /// Get the range of indices in the array for a given layer 
        /// of a d-ary heap.
        /// </summary>
        /// <param name="level">The layer of the heap, numbered as 
        /// 0, 1, 2, ... with 0 being the root layer.
        /// </param>
        /// <remarks>
        /// Given by the formulas: 
        /// Count = d^L; 
        /// Index = ∑_{k = 0 to L-1} d^k = (d^L - 1) / (d - 1).
        /// </remarks>
        /// <returns>
        /// The index of the first element of the layer, and the count
        /// of elements in that same layer.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (int Index, int Count) GetLayerRange(int level)
        {
            int count = 1 << (Log2OfWays * level);
            int index = (int)((uint)(count - 1) / WaysMinusOne);
            return (index, count);
        }

        /// <summary>
        /// Round up the capacity for the array representation of a 
        /// d-ary heap so that it can have a fully filled last layer.
        /// </summary>
        /// <param name="capacity">The desired capacity, to be rounded up. </param>
        /// <returns></returns>
        private static int RoundUpCapacity(int capacity)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity for a priority queue may not be negative. ");

            // Ensures the calculation from GetLayerFromIndex cannot overflow
            if (capacity > int.MaxValue / (int)WaysMinusOne)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity of a priority queue may not exceed (2^31 - 1)/7. ");

            if (capacity <= 1)
                return capacity;

            int level = GetLayerFromIndex(capacity - 1);
            (int layerIndex, int layerCount) = GetLayerRange(level);
            int newCapacity = layerIndex + layerCount;

            return newCapacity;
        }

        /// <summary>
        /// Ensures that unused keys in the d-ary heap are padded
        /// with <see cref="int.MinValue" /> so that
        /// they have no effect on the SIMD comparisons.
        /// </summary>
        /// <param name="keys">The array holding the keys of the d-ary heap. </param>
        /// <param name="count">The number of elements in the d-ary heap
        /// that are valid.  Padding starts after the last element.
        /// </param>
        private static void PadKeysAtEnd(int[] keys, int count)
        {
            Array.Fill(keys, int.MinValue, count, keys.Length - count);
        }

        /// <summary>
        /// Clear the heap and add many elements into it in one shot.
        /// </summary>
        /// <typeparam name="TState">Type of the 
        /// arbitrary state to pass into <paramref cref="action"/>. 
        /// </typeparam>
        /// <param name="capacity">
        /// The capacity that the priority heap should be allocated for.
        /// </param>
        /// <param name="state">
        /// An arbitrary state that is passed through into <paramref name="populateFunc" />.
        /// </param>
        /// <param name="populateFunc">
        /// This user-defined function is to fill the heap's arrays via
        /// the references passed in.
        /// </param>
        public void Initialize<TState>(int capacity, 
                                       ref TState state, 
                                       PopulateEntriesFunction<TState> populateFunc)
        {
            bool hasNewArrays = PrepareArrays(capacity, preserve: false);

            int oldCount = _count;
            _count = 0;

            int count = populateFunc.Invoke(ref state, 
                                            _keys.AsSpan()[0..capacity], 
                                            _values.AsSpan()[0..capacity]);

            if (count < 0 || count > capacity)
            {
                throw new InvalidOperationException(
                    "The user-specified function to populate a heap returned a count that is out of range. ");
            }

            _count = count;

            if (hasNewArrays || capacity > oldCount)
                PadKeysAtEnd(_keys, count);

            // Heap property is trivially satisfied for 0 or 1 elements.
            if (count <= 1)
                return;

            // Depth of the heap
            int level = GetLayerFromIndex(count - 1);

            // Index of first node, and count of nodes, at the deepest layer
            (int startIndex, int countAtLevel) = GetLayerRange(level);

            // Set indices on elements in deepest layer
            var values = _values;
            for (int j = 0; j < countAtLevel; ++j)
                InvokeIndexUpdateCallback(ref values[startIndex + j], startIndex + j);

            // Bubble down elements, in order from the second-deepest layer
            // to the top-most layer.
            do
            {
                // Move to the next upper layer, by moving startIndex
                // backwards by the number of elements in that layer
                --level;
                countAtLevel >>= Log2OfWays;
                startIndex -= countAtLevel;

                for (int j = 0; j < countAtLevel; ++j)
                {
                    InvokeIndexUpdateCallback(ref values[startIndex + j], startIndex + j);
                    BubbleDown(startIndex + j);
                }
                    
            } while (level > 0);
        }
    }
}
