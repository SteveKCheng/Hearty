using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace JobBank.Server
{
    /// <summary>
    /// Holds a result that is provided asynchronously, that can be queried by remote clients.
    /// </summary>
    /// <remarks>
    /// Conceptually, this class serves the same purpose as asynchronous tasks from the .NET
    /// standard library.  But this class is implemented in a way that instances can be 
    /// monitored and managed remotely, e.g. through ReST APIs or a Web UI.
    /// This class would typically be used for user-visible "jobs", whereas
    /// asynchronous tasks have to be efficient for local microscopic tasks
    /// within a .NET program.  So, this class tracks much more bookkeeping
    /// information.
    /// </remarks>
    public partial class Promise
    {
        public Payload? ResultPayload
        {
            get
            {
                if (_isFulfilled == 0)
                    return null;

                return _resultPayload;
            }
        }

        /// <summary>
        /// The time, in UTC, when this promise was first created.
        /// </summary>
        public DateTime CreationTime { get; }

        public Payload RequestPayload { get; }

        public bool IsCompleted => _isFulfilled != 0;

        public Promise(Payload requestPayload)
        {
            CreationTime = DateTime.UtcNow;
            RequestPayload = requestPayload;
        }

        private Payload _resultPayload;

        /// <summary>
        /// The .NET object that must be locked to safely 
        /// access most mutable fields in this object.
        /// </summary>
        internal object SyncObject => this;

        /// <summary>
        /// Fulfill this promise with a successful result.
        /// </summary>
        /// <remarks>
        /// Any subscribers to this promise are notified.
        /// </remarks>
        internal void PostResult(Payload resultPayload)
        {
            Debug.Assert(_isFulfilled == 0);

            _resultPayload = resultPayload;
            _isFulfilled = 1;   // Implies release fence to publish _resultPayload

            // Loop through subscribers to wake up one by one.
            // Releases the list lock after de-queuing each node,
            // before invoking its continuation (involving user-defined code).
            SubscriptionNode? node;
            while ((node = SubscriptionNode.PrepareToWakeUpNextSubscriber(this)) != null)
            {
                try
                {
                    node.TryMarkPublished();
                }
                catch
                {
                    // FIXME log the error, do not swallow
                }
            }
        }

        private volatile int _isFulfilled;

        // Expiry

        // List of clients that are watching this promise
        //
        // Client described by:
        //      IPromiseSubscriber that has a method to be invoked when result is posted
        //      handle # (should this be global or client-specific?)
        // Put in a lazily-allocated list
        //
        // It would be more efficient to have one dictionary per client: ??
        // IPromiseSubscriber:
        //      IPromiseSubscriber.AddPromise(Promise promise) -> int handle
        //      IPromiseSubscriber.RemovePromise(int handle)
        //      

    }
}
