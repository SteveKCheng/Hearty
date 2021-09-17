using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace JobBank.Server
{
    internal partial class Promise
    {
        /// <summary>
        /// Bookkeeping information for one subscription from a client to a <see cref="Promise"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This class also participates as a node in a doubly-linked list stored within
        /// the parent promise.  A linked list is used in preference to a array-backed
        /// list, because, for scalability addition and removal of entries should be O(1).
        /// A non-intrusive list is not used because other classes/structures outside
        /// the parent promise take need to take references to individual subscription data,
        /// while minimizing the number of GC allocations.
        /// </para>
        /// <para>
        /// Asynchronous operations on the promise need to be tracked with client-specific
        /// information and timeout/cancellation parameters, so this class also acts as a 
        /// (custom) task source provided to each client.
        /// </para>
        /// </remarks>
        internal class Subscription : IValueTaskSource<PromiseResult>
        {
            /// <summary>
            /// Backing field for <see cref="Previous" />.
            /// </summary>
            private Subscription? _previous;

            /// <summary>
            /// Backing field for <see cref="Next" />.
            /// </summary>
            private Subscription? _next;

            /// <summary>
            /// The subscription for a preceding client, within the doubly-linked 
            /// list stored in the same parent promise. 
            /// </summary>
            internal Subscription? Previous => _previous;

            /// <summary>
            /// The subscription for a following client, within the doubly-linked 
            /// list stored in the same parent promise. 
            /// </summary>
            internal Subscription? Next => _next;

            /// <summary>
            /// The (parent) promise object that is being subscribed to.
            /// </summary>
            public readonly Promise Promise;

            /// <summary>
            /// The subscriber that created this instance.
            /// </summary>
            public readonly Subscriber Subscriber;

            /// <summary>
            /// Function registered by <see cref="RegisterCallback"/>, if any,
            /// that has yet to be invoked.
            /// </summary>
            private Action<object?>? _callbackFunc;

            /// <summary>
            /// User-specified state object to pass into <see cref="_callbackFunc"/>.
            /// </summary>
            private object? _callbackArg;

            /// <summary>
            /// Initializes the object to store subscription data, but
            /// does not link it into the parent promise yet.
            /// </summary>
            public Subscription(Promise promise, in Subscriber subscriber)
            {
                Promise = promise;
                Subscriber = subscriber;
            }

            /// <summary>
            /// Detach this subscription from the doubly-linked list stored in its parent promise.
            /// </summary>
            public void DetachSelf()
            {
                lock (Promise.SyncObject)
                {
                    if (_previous != null) 
                        _previous._next = _next;
                    if (_next != null) 
                        _next._previous = _previous;
                    if (Promise._lastSubscription == this)
                        Promise._lastSubscription = _previous;

                    _previous = null;
                    _next = null;
                }
            }

            /// <summary>
            /// Register a function to be called when a full result has been posted
            /// to the parent promise.
            /// </summary>
            /// <remarks>
            /// If the promise has already completed, the callback is invoked synchronously.
            /// </remarks>
            public void RegisterCallback(Action<object?> callback, object? state)
            {
                lock (Promise.SyncObject)
                {
                    if (_callbackFunc != null)
                        throw new InvalidOperationException("Callback has already been registered. ");

                    if (!Promise.IsCompleted)
                    {
                        _callbackFunc = callback;
                        _callbackArg = state;

                        // Add self to parent's list
                        var previous = Promise._lastSubscription;
                        _previous = previous;
                        if (previous != null)
                            previous._next = this;
                        Promise._lastSubscription = this;

                        return;
                    }
                }

                // Parent has already completed
                _callbackStage = (int)CallbackStage.Completed;
                callback.Invoke(state);
                _timeoutTrigger.Dispose();
            }

            internal void InvokeRegisteredCallback(CallbackStage stage)
            {
                if ((CallbackStage)Interlocked.CompareExchange(ref _callbackStage,
                                                               (int)stage, 
                                                               (int)CallbackStage.Start) != CallbackStage.Start)
                    return;

                Action<object?>? callbackFunc;
                object? callbackArg;
                
                callbackFunc = _callbackFunc;
                callbackArg = _callbackArg;

                callbackFunc!.Invoke(callbackArg);
                _timeoutTrigger.Dispose();
            }

            #region Implementation of IValueTaskSource

            PromiseResult IValueTaskSource<PromiseResult>.GetResult(short token)
            {
                Payload? payload;

                try
                {
                    var stage = (CallbackStage)_callbackStage;

                    if (stage == CallbackStage.Cancelled)
                        _cancellationToken.ThrowIfCancellationRequested();
                    if (stage == CallbackStage.TimedOut)
                        throw new TimeoutException("Data for the promise was not available before timing out. ");

                    payload = Promise.ResultPayload;
                    if (payload == null)
                        throw new InvalidOperationException("Cannot call IValueTaskSource.GetResult on an uncompleted promise. ");
                }
                catch
                {
                    DetachSelf();
                    throw;
                }

                return new PromiseResult(this, payload.GetValueOrDefault());
            }

            ValueTaskSourceStatus IValueTaskSource<PromiseResult>.GetStatus(short token)
            {
                return Promise.IsCompleted ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;
            }

            void IValueTaskSource<PromiseResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                RegisterCallback(continuation, state);

                _cancellationToken.Register(s => ((Subscription)s!).InvokeRegisteredCallback(CallbackStage.Cancelled),
                                            this, false);   // FIXME respect flags
                _timeoutTrigger.Token.Register(s => ((Subscription)s!).InvokeRegisteredCallback(CallbackStage.TimedOut),
                                               this, false);
            }

            #endregion

            /// <summary>
            /// Prepare for timeout and cancellation as part of asynchronous retrieval
            /// of the parent promise's result.
            /// </summary>
            internal void PrepareForCancellation(in TimeSpan? timeout, CancellationToken cancellationToken)
            {
                _cancellationToken = cancellationToken;
                if (timeout != null)
                    _timeoutTrigger = CancellationPool.CancelAfter(timeout.GetValueOrDefault());
            }

            /// <summary>
            /// The canellation token passed in by the client as part of an asynchronously
            /// retrieving from the promise.
            /// </summary>
            private CancellationToken _cancellationToken;

            /// <summary>
            /// Triggers timeout if desired by the client.
            /// </summary>
            private CancellationPool.Use _timeoutTrigger;

            internal enum CallbackStage : int
            {
                Start = 0,
                Completed = 1,
                TimedOut = 2,
                Cancelled = 3
            }

            private int _callbackStage;
        }

        /// <summary>
        /// Points to the last subscription entry attached to this promise.
        /// </summary>
        private Subscription? _lastSubscription;

        /// <summary>
        /// Subscribes to this promise and asynchronously retrieve results from it.
        /// </summary>
        public ValueTask<PromiseResult> GetResultAsync(in Subscriber subscriber, in TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var payload = ResultPayload;
            if (payload != null)
                return new ValueTask<PromiseResult>(new PromiseResult(null, payload.GetValueOrDefault()));

            var subscription = new Subscription(this, subscriber);
            subscription.PrepareForCancellation(timeout, cancellationToken);
            return new ValueTask<PromiseResult>(subscription, token: 0);
        }

    }
}
