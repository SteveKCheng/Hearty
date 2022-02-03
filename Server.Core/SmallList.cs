using System;
using System.Collections;
using System.Collections.Generic;

namespace JobBank.Server
{
    /// <summary>
    /// Stores a list of items, optimizing for small
    /// lists, in particularly of one item only.
    /// </summary>
    /// <remarks>
    /// The contents for a list of one is stored 
    /// directly in this structure, thus avoiding
    /// memory allocation.
    /// </remarks>
    /// <typeparam name="T">The type of item to store
    /// in the list.
    /// </typeparam>
    internal readonly struct SmallList<T> : IReadOnlyList<T>
    {
        private readonly T _singleItem;
        private readonly T[]? _manyItems;

        /// <summary>
        /// Stores a single item.
        /// </summary>
        public SmallList(T item)
        {
            _singleItem = item;
            _manyItems = null;
        }

        /// <summary>
        /// Stores a list of items.
        /// </summary>
        public SmallList(T[] items)
        {
            _singleItem = default!;
            _manyItems = items;
        }

        /// <summary>
        /// Get the item at the given index.
        /// </summary>
        /// <param name="index">The index ranging from 0 to 
        /// <see cref="Count" /> minus one. </param>
        /// <returns>The desired item. </returns>
        public T this[int index] => 
            _manyItems is not null ? _manyItems[index] : _singleItem;

        /// <inheritdoc cref="IReadOnlyCollection{T}.Count" />
        public int Count => _manyItems?.Length ?? 1;

        /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />
        public IEnumerator<T> GetEnumerator()
        {
            if (_manyItems is not null)
            {
                foreach (var item in _manyItems)
                    yield return item;
            }
            else
            {
                yield return _singleItem;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
