using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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

        public PromiseOutput? RequestOutput { get; internal set; }

        public bool IsCompleted => _isFulfilled != 0;

        public PromiseId Id { get; }

        public Promise(DateTime creationTime, PromiseId id, PromiseOutput request)
        {
            Id = id;
            CreationTime = creationTime;
            RequestOutput = request;
        }

        public DateTime? Expiry { get; internal set; }

        public override int GetHashCode() => Id.GetHashCode();

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

        /// <summary>
        /// Awaits, in the background, for a job's result object to be published,
        /// and then forwards notifications to waiting subscribers.
        /// </summary>
        /// <remarks>
        /// This method should only be called exactly once.  The asynchronous
        /// output is not expected during construction because, when promises
        /// and jobs need to be cached, it is often necessary to generate
        /// the unique <see cref="PromiseId" /> to use as the cache key, 
        /// before the asynchronous work can start.
        /// </remarks>
        public void AwaitAndPostResult(in ValueTask<PromiseOutput> task)
        {
            if (Interlocked.Exchange(ref _hasAsyncResult, 1) != 0)
                throw new InvalidOperationException("Cannot post more than one asynchronous output into a promise. ");

            _ = PostResultAsync(task);
        }

        /// <summary>
        /// Calls <see cref="PostResult"/> with a job's output when the result task
        /// completes.
        /// </summary>
        /// <remarks>
        /// Since this method is only called by <see cref="AwaitAndPostResult"/>
        /// which discards the <see cref="Task" /> object, it could be declared
        /// as <c>async void</c> instead.  But <c>async void</c> is implemented
        /// behind the scenes by wrapping <c>async Task</c> and is in fact slightly
        /// less efficient.  Recent versions of .NET (Core) already reduce the
        /// number of allocations for <c>async</c> methods to one: 
        /// a single object works as <see cref="Task"/> and holds the data
        /// needed to continue the method after it suspends.  And if this
        /// method completes synchronously, then the pre-allocated
        /// <see cref="Task.CompletedTask"/> gets returned.
        /// </remarks>
        private async Task PostResultAsync(ValueTask<PromiseOutput> task)
        {
            var output = await task.ConfigureAwait(false);
            PostResult(output);
        }

        private volatile int _isFulfilled;

        /// <summary>
        /// Set to one the first time <see cref="AwaitAndPostResult" />
        /// is called.
        /// </summary>
        private int _hasAsyncResult;

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
