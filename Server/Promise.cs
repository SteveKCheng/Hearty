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
        public PromiseOutput? ResultOutput
        {
            get
            {
                if (_isFulfilled == 0)
                    return null;

                return _resultOutput;
            }
        }

        /// <summary>
        /// The time, in UTC, when this promise was first created.
        /// </summary>
        public DateTime CreationTime { get; }

        public PromiseOutput RequestOutput { get; }

        public bool IsCompleted => _isFulfilled != 0;

        public Promise(PromiseOutput request)
        {
            CreationTime = DateTime.UtcNow;
            RequestOutput = request;
        }

        private PromiseOutput? _resultOutput;

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
        internal void PostResult(PromiseOutput result)
        {
            Debug.Assert(_isFulfilled == 0);

            _resultOutput = result;
            _isFulfilled = 1;   // Implies release fence to publish _resultOutput

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
