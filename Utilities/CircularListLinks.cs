using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace JobBank.Utilities
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
    /// The items (often called "nodes") that form the linked list are objects
    /// that hold an instance of this structure.  Thus, an item can have its an object 
    /// reference taken, even if it has not yet been attached to the list yet.
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
    /// A single object can also be part of multiple lists, if it holds
    /// multiple instances of this structure, and there is a corresponding
    /// implementation of <typeparamref name="TLinksAccessor" /> for each
    /// instance.
    /// </item>
    /// <item>
    /// The number of items in the list is not tracked since some applications
    /// do not need it.  
    /// </item>
    /// <item>
    /// The list implemented here is intended to be used as a private member, and not
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
    /// <typeparam name="T">The type of items that form the list.
    /// </typeparam>
    /// <typeparam name="TLinksAccessor">
    /// Retrieves the interior reference to this structure for any
    /// potential list node.  This type is expected to be private.
    /// It cannot have instance data as its retrieval method needs
    /// to be called from the static methods defined in this structure.
    /// Thus it is necessarily constrained to be a value type,
    /// and a default-initialized instance is used to call 
    /// its interface method.
    /// </typeparam>
    public struct CircularListLinks<T, TLinksAccessor>
        where T : class
        where TLinksAccessor : struct, IInteriorStruct<T, CircularListLinks<T, TLinksAccessor>>
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
        /// Initialize for one node being in a list by itself.
        /// </summary>
        /// <param name="self">
        /// The node being initialized.
        /// </param>
        /// <remarks>
        /// This constructor should always be used to initialize
        /// the links of a node.  The default constructor does
        /// not initialize this structure correctly.
        /// </remarks>
        public CircularListLinks(T self)
        {
            _previous = _next = self;
        }

        /// <summary>
        /// Get the interior reference to the previous/next links
        /// of a circular-list node.
        /// </summary>
        /// <param name="self">The target node. </param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref CircularListLinks<T, TLinksAccessor> GetLinks(T self)
            => ref default(TLinksAccessor)!.GetInteriorReference(self);

        /// <summary>
        /// Append the sub-list into another, possibly empty list.
        /// </summary>
        /// <param name="self">
        /// Object reference to the node that heads the sub-list to append.
        /// </param>
        /// <param name="root">
        /// On entry to this method, this variable that holds the 
        /// reference to the node that is considered the first of the 
        /// other list, or null if that list is empty.  On return,
        /// this variable stores the first node of the combined list.
        /// </param>
        public static void Append(T self, ref T? root)
        {
            T? first = root;

            if (first is not null)
                MergeBefore(self, first);
            else
                root = self;
        }

        /// <summary>
        /// Prepend the sub-list into another, possibly empty list.
        /// </summary>
        /// <param name="self">
        /// Object reference to the node heading the sub-list to prepend.
        /// </param>
        /// <param name="root">
        /// Variable that stores the reference to the first node of the 
        /// other list, or null if that list is empty.  On return
        /// it stores the first node of the combined list.
        /// </param>
        public static void Prepend(T self, ref T? root)
        {
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
        public static void MergeBefore(T first, T target)
        {
            ref var firstLinks = ref GetLinks(first);
            ref var targetLinks = ref GetLinks(target);

            var mid = firstLinks._previous;
            GetLinks(mid)._next = target;

            var last = targetLinks._previous;
            GetLinks(last)._next = first;

            targetLinks._previous = mid;
            firstLinks._previous = last;
        }

        /// <summary>
        /// Remove a node from a list.
        /// </summary>
        /// <remarks>
        /// Precisely, the node to remove is separated 
        /// back into its own list of one.
        /// </remarks>
        /// <param name="self">
        /// Object reference to the node to remove.
        /// </param>
        /// <param name="root">
        /// Variable that stores the 
        /// reference to the node that is considered the first of the list,
        /// or null if the list is empty.
        /// </param>
        public static void Remove(T self, ref T? root)
        {
            ref var selfLinks = ref GetLinks(self);
            var next = selfLinks._next;

            GetLinks(selfLinks._previous)._next = next;
            GetLinks(next)._previous = selfLinks._previous;

            if (object.ReferenceEquals(root, self))
                root = !object.ReferenceEquals(next, self) ? next : null;

            selfLinks._previous = self;
            selfLinks._next = self;
        }
    }
}
