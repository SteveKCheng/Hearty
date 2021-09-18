using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace JobBank.Server
{
    /// <summary>
    /// Intrusively embeds links into a node to form a circularly-linked list
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation of a linked list differs from <see cref="LinkedList{T}"/>
    /// in several ways:  
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// It minimizes the number of GC allocations.  Only one allocation is needed
    /// for each item in the list.
    /// </item>
    /// <item>
    /// The items that form the linked list are implemented as classes inheriting
    /// from this base class.  Thus, an item can have its an object reference taken, 
    /// even if it has not yet been attached to the list yet, and it can implement
    /// interfaces at the same time.
    /// </item>
    /// <item>
    /// The list being circular allows iteration forwards from the first item
    /// or backwards from the last item, and also reduces the number of special
    /// cases required in the implementation.  This trick is borrowed from 
    /// the Linux kernel.
    /// </item>
    /// <item>
    /// A node can switch between different lists into which it is placed.
    /// A node that is not explicitly added to another list is considered
    /// to form its own list, of one.  And it is possible to merge one list
    /// of nodes into another.
    /// </item>
    /// <item>
    /// The number of items in the list is not tracked since some applications
    /// do not need it.  
    /// </item>
    /// <item>
    /// The list implemented here is intended to be used in internals, and not
    /// part of any public API.  The client shall take responsibility to ensure
    /// correctness, and to provide locking if the list is concurrently accessed.
    /// So very few validity checks are done here.
    /// </item>
    /// </list>
    /// <para>
    /// In summary, this implementation of a linked list is made to be 
    /// efficient and flexible, at the expense of being less easy to use
    /// and straightforward than the standard one.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The final type of an item in the list.  It should
    /// inherit from this base class.
    /// </typeparam>
    internal abstract class CircularListNode<T> where T: CircularListNode<T>
    {
        /// <summary>
        /// Backing field for <see cref="Previous"/>.
        /// </summary>
        private T _previous;

        /// <summary>
        /// Backing field for <see cref="Next"/>.
        /// </summary>
        private T _next;

        /// <summary>
        /// The preceding item in the list.
        /// </summary>
        public T Previous => _previous;

        /// <summary>
        /// The following item in the list.
        /// </summary>
        public T Next => _next;

        /// <summary>
        /// Initializes this node as forming its own list of one.
        /// </summary>
        protected CircularListNode()
        {
            _previous = _next = Unsafe.As<T>(this);
        }

        /// <summary>
        /// Append the sub-list which has this node as the first, 
        /// into another, possibly empty list.
        /// </summary>
        /// <param name="root">
        /// On entry to this method, this variable that holds the 
        /// reference to the node that is considered the first of the 
        /// other list, or null if that list is empty.  On return,
        /// this variable stores the first node of the combined list.
        /// </param>
        protected void AppendSelf(ref T? root)
        {
            var self = Unsafe.As<T>(this);
            T? first = root;

            if (first is not null)
                MergeBefore(self, first);
            else
                root = self;
        }

        /// <summary>
        /// Prepend the sub-list which has this node as the first, 
        /// into another, possibly empty list.
        /// </summary>
        /// <param name="root">
        /// Variable that stores the reference to the first node of the 
        /// other list, or null if that list is empty.  On return
        /// it stores the first node of the combined list.
        /// </param>
        protected void PrependSelf(ref T? root)
        {
            var self = Unsafe.As<T>(this);
            T? first = root;

            if (first is not null)
                MergeBefore(self, first);
  
            root = self;
        }

        /// <summary>
        /// Merge one list into another list.
        /// </summary>
        /// <param name="first">The node that is considered the first, 
        /// of the sub-list to merge in. </param>
        /// <param name="target">The node that should follow the first sub-list
        /// after it is merged into the combined list. </param>
        /// <remarks>
        /// <para>
        /// This method does nothing if <paramref name="first"/> is the same
        /// as <paramref name="target"/>.
        /// </para>
        /// <para>
        /// If <paramref name="first"/> is found in the list containing
        /// <paramref name="target"/>, then this method insteads splits 
        /// the latter list into two: one list containing the nodes
        /// from <paramref name="target"/> up to but not including
        /// <paramref name="first"/>, and the second list from
        /// <paramref name="first"/> to the last item in the original 
        /// list.
        /// </para>
        /// </remarks>
        protected static void MergeBefore(T first, T target)
        {
            var mid = first._previous;
            mid._next = target;

            var last = target._previous;
            last._next = first;

            target._previous = mid;
            first._previous = last;
        }

        /// <summary>
        /// Remove this node from a list.
        /// </summary>
        /// <remarks>
        /// Precisely, this node is separated back into its own list of one.
        /// </remarks>
        /// <param name="root">
        /// Variable that stores the 
        /// reference to the node that is considered the first of the list,
        /// or null if the list is empty.
        /// </param>
        protected void RemoveSelf(ref T? root)
        {
            this._previous._next = _next;
            this._next._previous = _previous;

            var self = Unsafe.As<T>(this);

            if (object.ReferenceEquals(root, self))
                root = !object.ReferenceEquals(_next, self) ? _next : null;

            _previous = _next = self;
        }
    }

    /// <summary>
    /// Exposes the list formed by <see cref="CircularListNode{T}"/> as a sequence in .NET.
    /// </summary>
    /// <remarks>
    /// <see cref="CircularListNode{T}"/> does not require a separate object to manage
    /// the list itself, so this structure is provided to represent the list as a whole,
    /// considered as a sequence in .NET.
    /// </remarks>
    /// <typeparam name="T">The type of item stored in the linked list. </typeparam>
    internal readonly struct CircularListView<T> : IEnumerable<T> where T: CircularListNode<T>
    {
        /// <summary>
        /// Points to the node considered to be the first in the list, or null
        /// if the list is empty.
        /// </summary>
        private readonly T? _first;

        /// <summary>
        /// Whether the items in list should be presented forwards or backwards,
        /// as a sequence.
        /// </summary>
        private readonly bool _forward;

        /// <summary>
        /// Defines the sequence from the circularly-linked list.
        /// </summary>
        /// <param name="first">The node that is considered the first item in the list,
        /// or null if the list is empty.
        /// </param>
        /// <param name="forward">
        /// If true, iterates forwards from the first node.  If false,
        /// iterates backwards from the last node.
        /// </param>
        public CircularListView(T? first, bool forward = true)
        {
            _first = first;
            _forward = forward;
        }

        /// <summary>
        /// Get the implementation of <see cref="IEnumerator{T}"/>
        /// without boxing, for efficiency.
        /// </summary>
        public Enumerator GetEnumerator() => new(_first, _forward);

        /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />.
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc cref="IEnumerable.GetEnumerator" />.
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Iterates through a list formed by <see cref="CircularListNode{T}"/>.
        /// </summary>
        public struct Enumerator : IEnumerator<T>
        {
            /// <summary>
            /// The item to present as the first in the sequence, or null if
            /// the sequence is empty.
            /// </summary>
            private readonly T? _begin;

            /// <summary>
            /// The current item to be reported in the sequence.
            /// </summary>
            private T? _current;

            /// <summary>
            /// Whether the original list is to be iterated forwards or backwards.
            /// </summary>
            private readonly bool _forward;

            /// <summary>
            /// Whether iteration has ended for the sequence.
            /// </summary>
            private bool _ended;

            /// <inheritdoc cref="IEnumerator{T}.Current" />.
            public T Current => _current ?? throw new InvalidOperationException();

            /// <inheritdoc cref="IEnumerator.Current" />.
            object IEnumerator.Current => Current;

            /// <summary>
            /// Does nothing.
            /// </summary>
            public void Dispose()
            {
            }

            /// <summary>
            /// Prepare to iterate through the list.
            /// </summary>
            /// <param name="first">The node that is considered the first item in the list,
            /// or null if the list is empty.
            /// </param>
            /// <param name="forward">
            /// If true, iterates forwards from the first node.  If false,
            /// iterates backwards from the last node.
            /// </param>
            public Enumerator(T? first, bool forward)
            {
                _begin = forward ? first : first?.Previous;
                _current = null;
                _forward = forward;
                _ended = _begin is null;
            }

            /// <inheritdoc cref="IEnumerator.MoveNext" />.
            public bool MoveNext()
            {
                if (_current is not null)
                {
                    _current = _forward ? _current.Next : _current.Previous;
                    if (!object.ReferenceEquals(_current, _begin))
                        return true;

                    _current = null;
                    _ended = true;
                }
                else if (!_ended)
                {
                    _current = _begin;
                    return true;
                }

                return false;
            }

            /// <inheritdoc cref="IEnumerator.Reset" />.
            public void Reset()
            {
                _current = null;
                _ended = _begin is null;
            }
        }
    }
}
