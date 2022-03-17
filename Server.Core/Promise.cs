using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server
{
    /// <summary>
    /// Translates a failure represented by a .NET exception 
    /// to output that can be stored in <see cref="Promise" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The translator should not throw an exception unless
    /// it should be considered a fatal one for the (server)
    /// application.
    /// </para>
    /// <para>
    /// An explicit delegate type is used so that it can 
    /// readily used in dependency injection.
    /// </para>
    /// </remarks>
    /// <param name="promiseId">
    /// The ID for the promise whose processing threw the exception,
    /// if any.
    /// </param>
    /// <param name="exception">
    /// The .NET exception object to translate.
    /// </param>
    /// <returns>
    /// A (partial) representation of the exception that can
    /// be stored in a promise (and transferred to remote clients,
    /// or persisted).
    /// </returns>
    public delegate PromiseData 
        PromiseExceptionTranslator(PromiseId? promiseId, Exception exception);

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
        /// <summary>
        /// The output object of the promise.
        /// </summary>
        /// <remarks>
        /// This property returns null if the promise has 
        /// not been fulfilled yet.
        /// </remarks>
        public PromiseData? ResultOutput
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

        /// <summary>
        /// The input object for the promise, if it has been provided
        /// on creation.
        /// </summary>
        public PromiseData? RequestOutput { get; internal set; }

        public bool IsCompleted => _isFulfilled != 0;

        /// <summary>
        /// True if the promise has completed and its output is transient.
        /// </summary>
        public bool IsTransient => IsCompleted && (RequestOutput?.IsTransient ?? false);

        /// <summary>
        /// The ID that has been assigned to this promise.
        /// </summary>
        public PromiseId Id { get; }

        /// <summary>
        /// Construct or re-materialize an in-memory representation of a promise.
        /// </summary>
        /// <param name="creationTime">
        /// The time that this promise was first created.
        /// </param>
        /// <param name="id">The ID assigned to the promise. </param>
        /// <param name="input">
        /// The input which is considered to create this promise. 
        /// This argument may be null if the input is not to be stored.
        /// </param>
        /// <param name="output">
        /// The output of this promise, if it is available synchronously.
        /// </param>
        public Promise(DateTime creationTime, 
                       PromiseId id, 
                       PromiseData? input, 
                       PromiseData? output)
        {
            Id = id;
            CreationTime = creationTime;
            RequestOutput = input;

            if (output is not null)
            {
                _resultOutput = output;
                _hasAsyncResult = 1;
                _isFulfilled = 1;
            }
        }

        public DateTime? Expiry { get; internal set; }

        /// <summary>
        /// Hash code based on <see cref="Id" />.
        /// </summary>
        public override int GetHashCode() => Id.GetHashCode();

        private PromiseData? _resultOutput;

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
        internal void PostResultInternal(PromiseData result)
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
        /// This method should only be called at most once.  The asynchronous
        /// output is not expected during construction because, when promises
        /// and jobs need to be cached, it is often necessary to generate
        /// the unique <see cref="PromiseId" /> to use as the cache key, 
        /// before the asynchronous work can start.
        /// </remarks>
        public void AwaitAndPostResult(in ValueTask<PromiseData> task,
                                       PromiseExceptionTranslator exceptionTranslator)
        {
            if (!TryAwaitAndPostResult(task, exceptionTranslator))
                throw new InvalidOperationException("Cannot post more than one asynchronous output into a promise. ");
        }

        /// <summary>
        /// Awaits, in the background, for a job's result object to be published,
        /// and then forwards notifications to waiting subscribers.  Does nothing
        /// if already called.
        /// </summary>
        /// <remarks>
        /// The asynchronous output is not expected during construction 
        /// of <see cref="Promise" />, because, when promises
        /// and jobs need to be cached, it is often necessary to generate
        /// the unique <see cref="PromiseId" /> to use as the cache key, 
        /// before the asynchronous work can start.
        /// </remarks>
        /// <param name="task">
        /// The .NET asynchronous task that will provide the result.
        /// </param>
        /// <param name="exceptionTranslator">
        /// Translates the exception to a serializable format if 
        /// <paramref name="task" /> completes with an exception.
        /// This function itself should not throw an exception.
        /// </param>
        /// <param name="postAction">
        /// A function that is called after this promise receives the result.
        /// The effect will be similar to <see cref="Task.ContinueWith(Action{Task, object?}, object?)"/>,
        /// but avoids the race condition where <paramref name="task" /> completes
        /// but this promise does not.
        /// </param>
        /// <returns>
        /// True if <paramref name="task" /> is successfully registered.
        /// False if this method has already been called; in that case
        /// <paramref name="postAction" /> is ignored.
        /// </returns>
        public bool TryAwaitAndPostResult(in ValueTask<PromiseData> task,
                                          PromiseExceptionTranslator exceptionTranslator,
                                          Action<Promise>? postAction = null)
        {
            if (Interlocked.Exchange(ref _hasAsyncResult, 1) != 0)
                return false;

            _ = PostResultAsync(task, exceptionTranslator, postAction);
            return true;
        }

        /// <summary>
        /// Calls <see cref="PostResultInternal"/> with a job's output when the result task
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
        private async Task PostResultAsync(ValueTask<PromiseData> task,
                                           PromiseExceptionTranslator exceptionTranslator,
                                           Action<Promise>? postAction)
        {
            PromiseData output;
            try
            {
                output = await task.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                output = exceptionTranslator.Invoke(Id, e);
            }

            PostResultInternal(output);
            postAction?.Invoke(this);
        }

        private volatile int _isFulfilled;

        /// <summary>
        /// Set to one the first time <see cref="AwaitAndPostResult" />
        /// is called.
        /// </summary>
        private int _hasAsyncResult;

        /// <summary>
        /// Whether some caller has already set this promise to receive
        /// a result, or the promise already has the result.
        /// </summary>
        public bool HasAsyncResult => _hasAsyncResult != 0;

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
