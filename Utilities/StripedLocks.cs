using System;
using System.Numerics;

namespace Hearty.Utilities
{
    /// <summary>
    /// Array of objects provided for locking.
    /// </summary>
    /// <remarks>
    /// Instances of this class are meant to be pre-allocated, so 
    /// that objects that need to lock their internal state do not need
    /// to allocate an extra object just for that purpose if it was
    /// not already allocating one already.
    /// </remarks>
    public sealed class StripedLocks
    {
        private readonly object[] _lockObjects;

        /// <summary>
        /// Create an array of lock objects sized appropriately
        /// to the degree of concurrency allowed in this process.
        /// </summary>
        public StripedLocks()
            : this(Environment.ProcessorCount)
        {
        }

        /// <summary>
        /// Create an array of lock objects.
        /// </summary>
        /// <param name="count">
        /// The number of lock objects in the array.  It will
        /// be rounded up to the nearest power of two,
        /// and it will be floored to 1 and capped to an
        /// implementation-defined limit.
        /// </param>
        public StripedLocks(int count)
        {
            count = Math.Max(Math.Min(count, short.MaxValue + 1), 1);

            uint countLockObjects = RoundUpToPowerOf2((uint)count);
            var lockObjects = new object[countLockObjects];
            lockObjects[0] = lockObjects;
            for (uint i = 1; i < countLockObjects; ++i)
                lockObjects[i] = new object();
            _lockObjects = lockObjects;
        }

        /// <summary>
        /// Select an object for locking at the specified index.
        /// </summary>
        /// <param name="index">
        /// Any index that the caller deems appropriate to identify 
        /// the lock to use.  This value is taken modulo the size
        /// of the allocated array of lock objects.
        /// </param>
        /// <returns>
        /// The object associated to the given index that can be locked.
        /// </returns>
        public object this[int index]
            => _lockObjects[(uint)index & ((uint)_lockObjects.Length - 1)];

        private static uint RoundUpToPowerOf2(uint value)
        {
#if NET6_0
            return BitOperations.RoundUpToPowerOf2(value);
#else
            --value;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
#endif
        }
    }

    /// <summary>
    /// Array of objects provided for locking, designated for a
    /// particular caller identified by its type name.
    /// </summary>
    /// <typeparam name="TCaller">
    /// Type name of the caller that would use the locks.
    /// </typeparam>
    public static class StripedLocks<TCaller>
    {
        /// <summary>
        /// Singleton instance of the array of lock objects
        /// for the caller identified by <typeparamref name="TCaller" />.
        /// </summary>
        public static StripedLocks Shared { get; } = new StripedLocks();
    }
}
