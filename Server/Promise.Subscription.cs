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
        /// (custom) task source provied to each client.
        /// </para>
        /// </remarks>
        internal class Subscription : IValueTaskSource<Payload>
        {
            /// <summary>
            /// The subscription for a preceding client, within the doubly-linked 
            /// list stored in the same parent promise. 
            /// </summary>
            private Subscription? _previous;

            /// <summary>
            /// The subscription for a following client, within the doubly-linked 
            /// list stored in the same parent promise. 
            /// </summary>
            private Subscription? _next;

            internal Subscription? Previous => _previous;

            internal Subscription? Next => _next;

            /// <summary>
            /// The client that is subscribing to the promise.
            /// </summary>
            public readonly IPromiseClientInfo Client;

            /// <summary>
            /// The (parent) promise object that is being subscribed to.
            /// </summary>
            public readonly Promise Promise;

            /// <summary>
            /// An arbitrary integer that the client can associate to the subscribed promise, 
            /// so that it can distinguish its other subscriptions.
            /// </summary>
            public readonly int Handle;

            /// <summary>
            /// Function registered by <see cref="RegisterCallback"/>, if any,
            /// that has yet to be invoked.
            /// </summary>
            private Action<object?>? _callbackFunc;

            /// <summary>
            /// User-specified state object to pass into <see cref="_callbackFunc"/>.
            /// </summary>
            private object? _callbackArg;
 
            public Subscription(Promise promise, IPromiseClientInfo client, int handle)
            {
                Client = client;
                Handle = handle;
                Promise = promise;

                // Add self to list
                lock (Promise.SyncObject)
                {
                    var previous = Promise._lastSubscription;
                    _previous = previous;
                    if (previous != null)
                        previous._next = this;
                    Promise._lastSubscription = this;
                }
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
                if (Interlocked.CompareExchange(ref _callbackStage, (int)CallbackStage.Preparing, (int)CallbackStage.Unset)
                    != (int)CallbackStage.Unset)
                {
                    throw new InvalidOperationException("Callback has already been registered. ");
                }

                lock (Promise.SyncObject)
                {
                    Debug.Assert(_callbackFunc == null);

                    if (!Promise.IsCompleted)
                    {
                        _callbackFunc = callback;
                        _callbackArg = state;
                        _callbackStage = (int)CallbackStage.Set;
                        return;
                    }
                }

                _callbackStage = (int)CallbackStage.Completed;
                callback.Invoke(state);
                _timeoutTrigger.Dispose();
            }



            internal void InvokeRegisteredCallback(CallbackStage stage)
            {
                var oldStage = (CallbackStage)Interlocked.CompareExchange(ref _callbackStage, (int)stage, (int)CallbackStage.Set);
                if (oldStage != CallbackStage.Set)
                    return;

                Action<object?>? callbackFunc;
                object? callbackArg;
                
                callbackFunc = _callbackFunc;
                callbackArg = _callbackArg;

                _callbackFunc = null;
                _callbackArg = null;
 
                callbackFunc!.Invoke(callbackArg);
                _timeoutTrigger.Dispose();
            }

            Payload IValueTaskSource<Payload>.GetResult(short token)
            {
                var stage = (CallbackStage)_callbackStage;
                if (stage == CallbackStage.Cancelled)
                    _cancellationToken.ThrowIfCancellationRequested();
                if (stage == CallbackStage.TimedOut)
                    throw new TimeoutException("Data for the promise was not available before timing out. ");

                var payload = Promise.ResultPayload;
                if (payload == null)
                    throw new InvalidOperationException("Cannot call IValueTaskSource.GetResult on an uncompleted promise. ");
                return payload.GetValueOrDefault();
            }

            ValueTaskSourceStatus IValueTaskSource<Payload>.GetStatus(short token)
            {
                return Promise.IsCompleted ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;
            }

            void IValueTaskSource<Payload>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                RegisterCallback(continuation, state);

                _cancellationToken.Register(s => ((Subscription)s!).InvokeRegisteredCallback(CallbackStage.Cancelled),
                                            this, false);   // FIXME respect flags
                _timeoutTrigger.Token.Register(s => ((Subscription)s!).InvokeRegisteredCallback(CallbackStage.TimedOut),
                                               this, false);
            }

            internal void PrepareForCancellation(TimeSpan? timeout, CancellationToken cancellationToken)
            {
                // FIXME locking?

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
                Unset = 0,
                Preparing = 1,
                Set = 2,
                Completed = 2,
                TimedOut = 3,
                Cancelled = 4
            }

            private int _callbackStage;
        }

        /// <summary>
        /// Points to the last subscription entry attached to this promise.
        /// </summary>
        private Subscription? _lastSubscription;

        public ValueTask<Payload> GetResultAsync(SubscriptionRegistration subscription, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var s = subscription.Subscription;
            if (s.Promise != this)
                throw new ArgumentException("Given subscription is not for this promise object. ", nameof(subscription));
            s.PrepareForCancellation(timeout, cancellationToken);
            return new ValueTask<Payload>(s, token: 0);
        }

    }
}
