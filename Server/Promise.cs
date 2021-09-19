using System;
using System.Buffers;
using System.Collections.Generic;

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
        private static readonly ArrayPool<SubscriptionNode> SubscriptionArrayPool
            = ArrayPool<SubscriptionNode>.Create();

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
        public void PostResult(Payload resultPayload)
        {
            const int initialCapacity = 1024;
            int count = 0;
            var rentedArray = SubscriptionArrayPool.Rent(initialCapacity);
            var subscriptions = rentedArray;

            try
            {
                lock (this.SyncObject)
                {
                    if (_isFulfilled != 0)
                        throw new PromiseException("Cannot post into a promise that has already been posted. ");

                    _resultPayload = resultPayload;
                    _isFulfilled = 1;

                    foreach (var s in new CircularListView<SubscriptionNode>(_firstSubscription))
                    {
                        if (count == subscriptions.Length)
                        {
                            var newSubscriptions = new SubscriptionNode[subscriptions.Length * 2];
                            subscriptions.CopyTo(newSubscriptions.AsSpan());
                            subscriptions = newSubscriptions;
                        }

                        subscriptions[count++] = s;
                    }
                }

                for (int i = 0; i < count; ++i)
                {
                    try
                    {
                        subscriptions[i].SetPublished();
                    }
                    catch
                    {
                        // FIXME log error
                        throw;
                    }
                }
            }
            finally
            {
                rentedArray.AsSpan().Slice(0, Math.Min(initialCapacity, count)).Clear();
                SubscriptionArrayPool.Return(rentedArray);
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
