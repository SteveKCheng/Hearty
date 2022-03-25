using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server
{
    /// <summary>
    /// Abstracts out the storage of promises in Hearty.
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
        private ulong _currentId;

        /// <summary>
        /// Create a promise object with the specified input.
        /// </summary>
        public abstract Promise CreatePromise(PromiseData? input,
                                              PromiseData? output = null);

        /// <summary>
        /// Instantiate an object of type <see cref="Promise" />,
        /// used to implement <see cref="CreatePromise" />.
        /// </summary>
        /// <param name="input">
        /// Input data to initialize the promise with.  
        /// </param>
        /// <param name="output">
        /// Output data to initialize the promise with, if synchronously
        /// available.
        /// </param>
        /// <returns>
        /// Promise object with a unique ID.
        /// </returns>
        protected Promise CreatePromiseObject(PromiseData? input,
                                              PromiseData? output)
        {
            var newId = new PromiseId(Interlocked.Increment(ref _currentId));
            var currentTime = DateTime.UtcNow;
            var promise = new Promise(currentTime, newId, input, output);
            return promise;
        }

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
        /// <para>
        /// Expiry allows an implementation to not use unbounded
        /// amounts of memory to store promises, that users are
        /// unlikely to access again.
        /// </para>
        /// <para>
        /// Expirations are necessarily processed in the background.  
        /// If queuing the promise to expire is implemented as an 
        /// asynchronous operation, that too can occur in the background
        /// so this method is not defined to return an asynchronous task.
        /// </para>
        /// </remarks>
        /// <param name="promise">The promise to be expiring later. </param>
        /// <param name="expiry">Suggested time when the promise can expire. </param>
        public abstract void SchedulePromiseExpiry(Promise promise,
                                                   DateTime expiry);

        /// <summary>
        /// The type of promise operation when logging an event.
        /// </summary>
        public enum OperationType
        {
            /// <summary>
            /// A new promise is being created.
            /// </summary>
            Create,

            /// <summary>
            /// The expiration time for a promise being changed.
            /// </summary>
            ScheduleExpiry
        }

        /// <summary>
        /// Information about an event being logged.
        /// </summary>
        public readonly struct EventArgs
        {
            /// <summary>
            /// The type of operation that triggered logging of this event.
            /// </summary>
            public OperationType Type { get; init; }

            /// <summary>
            /// The ID of the promise being subject to the operation.
            /// </summary>
            public PromiseId PromiseId { get; init; }
        }

        /// <summary>
        /// Receives simple logs of non-trivial operations as they occur.
        /// </summary>
        public event EventHandler<EventArgs>? OnStorageEvent;

        /// <summary>
        /// Call any registered event handlers to log an operation on
        /// the promise storage, in a safe manner.
        /// </summary>
        protected void InvokeOnStorageEvent(in EventArgs args)
        {
            try
            {
                OnStorageEvent?.Invoke(this, args);
            }
            catch (Exception)
            {
                // FIXME log this?
            }
        }
    }
}
