﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Abstracts out the storage of promises in Job Bank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The in-memory representation of currently "active" promises
    /// is <see cref="Promise" />.  But an implementation of this class
    /// may decide to "page out" those objects to external storage,
    /// to allow storing more promises than possible in in-process memory,
    /// for persistence, or for high availability.
    /// </para>
    /// <para>
    /// The public methods in this class must be thread-safe.
    /// </para>
    /// </remarks>
    public abstract class PromiseStorage
    {
        /// <summary>
        /// Create an initially empty promise object that can a
        /// posted result later.
        /// </summary>
        public abstract Promise CreatePromise();

        /// <summary>
        /// Retrieve the promise object that had been assigned
        /// the given ID.
        /// </summary>
        /// <remarks>
        /// If the promise object has been swapped out to external
        /// storage (i.e. not in managed GC memory), this method
        /// needs to re-materialize the object.
        /// </remarks>
        /// <returns>
        /// The promise object for the given ID, or null if no
        /// such promise exists.
        /// </returns>
        public abstract Promise? GetPromiseById(PromiseId id);

        /// <summary>
        /// Schedule a promise to expire at a future date/time.
        /// </summary>
        /// <remarks>
        /// Expiry allows an implementation to not use unbounded
        /// amounts of memory to store promises, that users are
        /// unlikely to access again.
        /// </remarks>
        /// <param name="promise">The promise to be expiring later. </param>
        /// <param name="when">Suggested time when the promise can expire. </param>
        public abstract void SchedulePromiseExpiry(Promise promise,
                                                   DateTime when);
    }
}
