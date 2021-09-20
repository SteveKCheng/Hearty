using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace JobBank.Server
{
    public partial class Promise
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
        internal class SubscriptionNode : CircularListNode<SubscriptionNode>, IValueTaskSource<PromiseResult>
        {
            /// <summary>
            /// The (parent) promise object that is being subscribed to.
            /// </summary>
            public readonly Promise Promise;

            /// <summary>
            /// The client that is subscribing to the promise.
            /// </summary>
            public IPromiseClientInfo Client { get; }

            /// <summary>
            /// An arbitrary integer that the client can associate to the subscribed promise, 
            /// so that it can distinguish its other subscriptions.
            /// </summary>
            /// <remarks>
            /// One client may subscribe to many promises.  We do not identify each
            /// individual subscriptions as abstract "clients" because 
            /// real clients need to be associated with users or
            /// connections (for authentication and monitoring), which tend
            /// to be heavier objects.
            /// </remarks>
            public uint Index { get; private set; }

            /// <summary>
            /// Manages the continuation passed in from <see cref="IValueTaskSource{TResult}.OnCompleted" />.
            /// </summary>
            private ValueTaskContinuation _continuation;

            /// <summary>
            /// Initializes the object to store subscription data, but
            /// does not link it into the parent promise yet.
            /// </summary>
            public SubscriptionNode(Promise promise, IPromiseClientInfo client)
            {
                Promise = promise;
                Client = client;
            }

            internal void AttachSelfWithoutWakeUp()
            {
                Debug.Assert(!_inWakeUpList);
                lock (Promise.SyncObject)
                {
                    if (_isAttached)
                        return;

                    base.AppendSelf(ref Promise._firstSubscription);
                    _isAttached = true;

                }

                Index = Client.OnSubscribe(new Subscription(this));
            }

            /// <summary>
            /// Detach this subscription from the doubly-linked list stored in its parent promise.
            /// </summary>
            public void DetachSelf()
            {
                lock (Promise.SyncObject)
                {
                    if (!_isAttached)
                        return;

                    base.RemoveSelf(ref (_inWakeUpList ? ref Promise._firstToWake
                                                       : ref Promise._firstSubscription));
                    _inWakeUpList = false;
                    _isAttached = false;
                }

                Client.OnUnsubscribe(new Subscription(this), Index);
            }

            /// <summary>
            /// Retrieves the next subscriber to wake up, and moves it to the non-wake-up list
            /// of subscribers.
            /// </summary>
            /// <remarks>
            /// This operation is performed under a very short-lived lock to make
            /// it safe, yet efficient, to wake up subscribers when subscriptions 
            /// can be added or removed concurrently.  In particular, we can release
            /// the lock after each subscriber is "de-queued", and the list lock 
            /// certainly will not be held when invoking the continuation.
            /// </remarks>
            /// <returns>
            /// The next subscriber in the list after it has been "de-queued" from
            /// the wake-up list.
            /// </returns>
            internal static SubscriptionNode? PrepareToWakeUpNextSubscriber(Promise promise)
            {
                SubscriptionNode? target;
                lock (promise.SyncObject)
                {
                    target = promise._firstToWake;

                    if (target != null)
                    {
                        Debug.Assert(target._isAttached && target._inWakeUpList);

                        target.RemoveSelf(ref promise._firstToWake);
                        target._inWakeUpList = false;
                        target.AppendSelf(ref promise._firstSubscription);
                    }
                }

                return target;
            }

            internal void SetPublished() => SetStatus(CallbackStage.Completed);

            private void SetStatus(CallbackStage stage)
            {
                if ((CallbackStage)Interlocked.CompareExchange(ref _callbackStage,
                                                               (int)stage, 
                                                               (int)CallbackStage.Start) != CallbackStage.Start)
                    return;

                // FIXME these can race with registration!
                _cancelTokenRegistration.Dispose();
                _timeoutTokenRegistration.Dispose();

                _continuation.Invoke(forceAsync: true);
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

                    if (!_isAttached)
                        AttachSelfWithoutWakeUp();
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

            void IValueTaskSource<PromiseResult>.OnCompleted(Action<object?> action, object? argument, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                // Capture the continuation outside the lock below.
                // We do not bother checking Promise.IsCompleted before embarking on this work,
                // because the user of ValueTask should be doing that already.
                var continuation = new ValueTaskContinuation(action, argument, flags);

                lock (Promise.SyncObject)
                {
                    if (_continuation.IsValid)
                        throw new InvalidOperationException("Callback has already been registered. ");

                    if (!Promise.IsCompleted)
                    {
                        // Move continuation into instance member
                        _continuation = continuation;
                        continuation = default;

                        // Add self to parent's list
                        _inWakeUpList = true;
                        _isAttached = true;
                        base.AppendSelf(ref Promise._firstToWake);
                    }
                }

                // Promise is already completed after we checked inside the lock above
                if (continuation.IsValid)
                {
                    // Same as SetStatus but we can execute the continuation synchronously.
                    // There can be no concurrent writes on _callbackStage, unless the ValueTask
                    // is improperly accessed, but even if so they would be harmless.
                    _callbackStage = (int)CallbackStage.Completed;
                    continuation.InvokeIgnoringExecutionContext(forceAsync: false);

                    _timeoutTrigger.Dispose();
                    return;
                }

                Index = Client.OnSubscribe(new Subscription(this));

                // Register cancellation callbacks.
                // But do not do so when result is already immediately available, as we
                // would have to do extra work to clean it up.
                _cancelTokenRegistration = _cancellationToken.UnsafeRegister(
                                                s => ((SubscriptionNode)s!).SetStatus(CallbackStage.Cancelled),
                                                this);
                _timeoutTokenRegistration = _timeoutTrigger.Token.UnsafeRegister(
                                                s => ((SubscriptionNode)s!).SetStatus(CallbackStage.TimedOut),
                                                this);
            }

            private CancellationTokenRegistration _cancelTokenRegistration;

            private CancellationTokenRegistration _timeoutTokenRegistration;

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

            /// <summary>
            /// True when this node has been queued under <see cref="Promise._firstToWake"/>;
            /// otherwise false.
            /// </summary>
            private bool _inWakeUpList;

            private bool _isAttached;
        }

        /// <summary>
        /// Points to the first of subscription entries attached to this promise
        /// that are not waiting for something to be published.
        /// </summary>
        private SubscriptionNode? _firstSubscription;

        /// <summary>
        /// Points to the first of subscription entries attached to this promise
        /// that are waiting for <see cref="PromiseResult"/> to be published.
        /// </summary>
        /// <remarks>
        /// This list is kept separate from <see cref="_firstSubscription" />
        /// to avoid having to lock the entire list when calling continuations
        /// for <see cref="IValueTaskSource{PromiseResult}" />.  
        /// That may not be good for performance since there can be many nodes.
        /// The nodes in this list are, necessarily, disjoint from those in 
        /// <see cref="_firstSubscription" />.  
        /// </remarks>
        private SubscriptionNode? _firstToWake;

        /// <summary>
        /// Subscribes to this promise and asynchronously retrieve results from it.
        /// </summary>
        public ValueTask<PromiseResult> GetResultAsync(IPromiseClientInfo client, in TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var subscription = new SubscriptionNode(this, client);

            var payload = ResultPayload;
            if (payload != null)
            {
                subscription.AttachSelfWithoutWakeUp();
                return new ValueTask<PromiseResult>(new PromiseResult(subscription, payload.GetValueOrDefault()));
            }

            subscription.PrepareForCancellation(timeout, cancellationToken);
            return new ValueTask<PromiseResult>(subscription, token: 0);
        }
    }
}
