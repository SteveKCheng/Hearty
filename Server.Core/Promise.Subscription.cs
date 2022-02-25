using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Hearty.Utilities;

namespace Hearty.Server
{
    using SubscriptionNodeLinks = CircularListLinks<Promise.SubscriptionNode,
                                               Promise.SubscriptionNode.ListLinksAccessor>;

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
        internal class SubscriptionNode : IValueTaskSource<SubscribedResult>
        {
            /// <summary>
            /// Accesses <see cref="SubscriptionNode._listLinks" /> in order to use
            /// <see cref="CircularListLinks{T, TLinksAccessor}" />.
            /// </summary>
            internal struct ListLinksAccessor : IInteriorStruct<SubscriptionNode, SubscriptionNodeLinks>
            {
                public ref SubscriptionNodeLinks GetInteriorReference(SubscriptionNode parent)
                    => ref parent._listLinks;
            }

            /// <summary>
            /// Forms the linked list of all subscription nodes.
            /// </summary>
            private SubscriptionNodeLinks _listLinks;

            /// <summary>
            /// The (parent) promise object that is being subscribed to.
            /// </summary>
            public readonly Promise Promise;

            /// <summary>
            /// The client that is subscribing to the promise.
            /// </summary>
            public readonly IPromiseClientInfo Client;

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
            /// <remarks>
            /// To avoid taking locks twice when the ValueTask that this node backs
            /// is awaited, we do not register the node at construction.
            /// </remarks>
            private SubscriptionNode(Promise promise, IPromiseClientInfo client)
            {
                Promise = promise;
                Client = client;
                _listLinks = new SubscriptionNodeLinks(this);
            }

            /// <summary>
            /// Returns the asynchronous task backed by a new instance of this class.
            /// </summary>
            /// <remarks>
            /// This code is put into a static method that can access private data of the new instance,
            /// to coordinate concurrency much more easily.
            /// </remarks>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods")]
            internal static ValueTask<SubscribedResult> CreateValueTask(Promise promise, 
                                                                     IPromiseClientInfo client,
                                                                     in TimeSpan? timeout,
                                                                     CancellationToken cancellationToken)
            {
                var self = new SubscriptionNode(promise, client);

                self.CancellationToken = cancellationToken;
                if (timeout is TimeSpan t)
                    self._timeoutTrigger = CancellationSourcePool.CancelAfter(t);

                var stage = self.Poll();
                self._callbackStage = (int)stage;
                
                if (stage != CallbackStage.Start)
                {
                    var output = promise.ResultOutput;
                    self.AttachSelfWithoutWakeUp();
                    return new ValueTask<SubscribedResult>(new SubscribedResult(self, output!));
                }

                return new ValueTask<SubscribedResult>(self, token: 0);
            }

            private void AttachSelfWithoutWakeUp()
            {
                Debug.Assert(!_inWakeUpList);
                lock (Promise.SyncObject)
                {
                    if (_isAttached)
                        return;

                    SubscriptionNodeLinks.Append(this, ref Promise._firstSubscription);
                    _isAttached = true;
                }

                // Since the ValueTask is known to be complete by now, we do not
                // need any more timeout.  As in SetStatus, cancel the timeout so
                // to let the CancellationTokenSource, unless the timeout already
                // has been triggered.
                _timeoutTrigger.Dispose();

                Index = Client.OnSubscribe(new Subscription(this));
            }

            /// <summary>
            /// Detach this subscription from the doubly-linked list stored in its parent promise.
            /// </summary>
            internal void Dispose()
            {
                if (!_isAttached && !_inWakeUpList)
                    return;

                bool wasAttached;

                lock (Promise.SyncObject)
                {
                    wasAttached = _isAttached;
                    if (!wasAttached && !_inWakeUpList)
                        return;

                    SubscriptionNodeLinks.Remove(
                        this, 
                        ref (_inWakeUpList ? ref Promise._firstToWake
                                           : ref Promise._firstSubscription));

                    _inWakeUpList = false;
                    _isAttached = false;
                }

                // If _inWakeUpList is true but wasAttached is false, that means this call
                // to Dispose is improperly racing with IValueTaskSource.OnCompleted,
                // and we cannot do much but skip some cleaning up.
                if (!wasAttached)
                    return;

                Client.OnUnsubscribe(new Subscription(this), Index);
                _cancelTokenRegistration.Dispose();
                _timeoutTokenRegistration.Dispose();
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

                        SubscriptionNodeLinks.Remove(target, ref promise._firstToWake);
                        target._inWakeUpList = false;
                        SubscriptionNodeLinks.Append(target, ref promise._firstSubscription);
                    }
                }

                return target;
            }

            /// <summary>
            /// Called as part of waking up this node when there is 
            /// a result from <see cref="Promise"/> to publish.
            /// </summary>
            /// <returns>True if this node accepts the promise's result.  False if
            /// asynchronous cancellation or timeout occurred first.
            /// </returns>
            internal void TryMarkPublished() => TryTransitioningStage(CallbackStage.Completed);

            /// <summary>
            /// Attempt to transition this node and the ValueTask it backs to a 
            /// (completed) stage.
            /// </summary>
            /// <remarks>
            /// There are multiple triggers for the ValueTask to complete because there can be
            /// asynchronous cancellation or timeout, which is specific to the current subscriber
            /// and does not come from <see cref="Promise"/>.  Thus there can be racing callers
            /// to this method, and this method will only allow the transition on the first call.
            /// Subsequent calls do nothing.
            /// </remarks>
            /// <returns>True if this is the first call (and the transition succeeds), false
            /// otherwise.
            /// </returns>
            private bool TryTransitioningStage(CallbackStage stage)
            {
                if ((CallbackStage)Interlocked.CompareExchange(ref _callbackStage,
                                                               (int)stage, 
                                                               (int)CallbackStage.Start) != CallbackStage.Start)
                    return false;

                // Cancel the timeout as soon as possible to let the CancellationTokenSource
                // to be returned back to the pool, if timeout has not already been triggered.
                _timeoutTrigger.Dispose();

                _continuation.Invoke(forceAsync: true);

                return true;
            }

            #region Implementation of IValueTaskSource

            /// <summary>
            /// Retrieve the result of the ValueTask which forwards the result from the parent promise,
            /// or indicates subscriber-specific cancellation.
            /// </summary>
            SubscribedResult IValueTaskSource<SubscribedResult>.GetResult(short token)
            {
                var stage = (CallbackStage)_callbackStage;

                if (stage == CallbackStage.Completed)
                {
                    return new SubscribedResult(this, Promise.ResultOutput);
                }
                else if (stage == CallbackStage.Start)
                {
                    // Should we dispose ourselves here, since the caller cannot do so? 
                    throw new InvalidOperationException("Cannot call IValueTaskSource.GetResult on an uncompleted promise. ");
                }
                else
                {
                    return new SubscribedResult(this, stage == CallbackStage.Cancelled ? PromiseStatus.ClientCancelled
                                                                                    : PromiseStatus.ClientTimedOut);
                }
            }

            /// <summary>
            /// Get the status of the ValueTask that this node is backing.
            /// </summary>
            /// <remarks>
            /// The ValueTask is considered complete if the parent promise publishes
            /// something, or cancellation happened specifically for the current subscriber 
            /// (and not necessarily in the parent promise).
            /// </remarks>
            ValueTaskSourceStatus IValueTaskSource<SubscribedResult>.GetStatus(short token)
                => ((CallbackStage)_callbackStage) != CallbackStage.Start
                        ? ValueTaskSourceStatus.Succeeded
                        : ValueTaskSourceStatus.Pending;

            /// <summary>
            /// Registers the continuation to invoke when the ValueTask completes asynchronously,
            /// or invokes it synchronous if it has already completed.
            /// </summary>
            /// <remarks>
            /// This method will also register this node under the parent promise, if not already.
            /// </remarks>
            void IValueTaskSource<SubscribedResult>.OnCompleted(Action<object?> action, object? argument, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                // Capture the continuation outside the lock below.
                //
                // Some work that we do not strictly need would get done should the ValueTask
                // be already complete at this point, but the caller (the user of ValueTask or
                // the compiler-generated code for C# await) should have short-circuited out
                // the call to this method in the case, anyway.
                var continuation = new ValueTaskContinuation(action, argument, flags);

                var stage = (CallbackStage)_callbackStage;

                // Completion of the ValueTask may be asynchronous.
                if (stage == CallbackStage.Start)
                {
                    // Protect the wake-up list in the parent promise, and ensure no partial
                    // continuation state for this node is observed when the parent wakes it up.
                    lock (Promise.SyncObject)
                    {
                        if (_continuation.IsValid)
                            throw new InvalidOperationException("Callback has already been registered. ");

                        // We poll for cancellation here, and do not register asynchronous callbacks
                        // for those yet.  Otherwise, such callbacks can race with the instance mbmer
                        // _continuation being set below.
                        stage = Poll();

                        if (stage == CallbackStage.Start)
                        {
                            // Move continuation into instance member.
                            _continuation = continuation;
                            continuation = default;

                            // Add self to parent's wake-up list
                            _inWakeUpList = true;
                            SubscriptionNodeLinks.Append(this, ref Promise._firstToWake);
                        }
                        else
                        {
                            // To invoke continuation synchronously, below.
                            _callbackStage = (int)stage;
                        }
                    }
                }

                // This code continues what needs to be done when this node is to be woken up
                // by the parent, but also needs to be done outside the lock, as large
                // amounts of code can get run here.
                //
                // Note this code can only be called at most once because we protect against 
                // registering more than one continuation.
                if (stage == CallbackStage.Start)
                {
                    // This call must not have happened yet since the only other way
                    // it is made is in AttachSelfWithoutWakeUp, which must not have
                    // occurred since this ValueTask is not yet complete.
                    Index = Client.OnSubscribe(new Subscription(this));

                    // Register cancellation callbacks.
                    // We do not do so when the result is already immediately available,
                    // as we would end up doing extra work to cancel these registrations.
                    _cancelTokenRegistration = CancellationToken.UnsafeRegister(
                                                    s => ((SubscriptionNode)s!).TryTransitioningStage(CallbackStage.Cancelled),
                                                    this);
                    _timeoutTokenRegistration = _timeoutTrigger.Token.UnsafeRegister(
                                                    s => ((SubscriptionNode)s!).TryTransitioningStage(CallbackStage.TimedOut),
                                                    this);

                    // When this node is to be in the wake-up list, set this flag only all
                    // registration is done to avoid data races on the three instance members
                    // that have just been set above, when Dispose is called concurrently. 
                    // That would be an abuse of the ValueTask but we want to be defensive.
                    Volatile.Write(ref _isAttached, true);
                }

                // When we reach here, the ValueTask is already in a completed state,
                // and this node was NOT placed in the parent's wake-up list.
                else
                {
                    // This node may not yet have been attached to the parent if we are
                    // recognizing the completion of the ValueTask for the first time here,
                    // i.e. it raced to complete while the lock was held above but not before.
                    if (!_isAttached)
                        AttachSelfWithoutWakeUp();

                    // Same as SetStatus but we can execute the continuation synchronously.
                    continuation.InvokeIgnoringExecutionContext(forceAsync: false);
                }
            }

            #endregion

            /// <summary>
            /// For de-registering, when this node is disposed, the callback for asynchronous 
            /// cancellation from <see cref="CancellationToken"/>.
            /// </summary>
            private CancellationTokenRegistration _cancelTokenRegistration;

            /// <summary>
            /// For de-registering, when this node is disposed, the callback triggered by
            /// asynchronous timeout from <see cref="_timeoutTrigger" />. 
            /// </summary>
            private CancellationTokenRegistration _timeoutTokenRegistration;

            /// <summary>
            /// The canellation token passed in by the client as part of an asynchronously
            /// retrieving from the promise.
            /// </summary>
            public CancellationToken CancellationToken { get; private set; }

            /// <summary>
            /// Triggers timeout if desired by the client.
            /// </summary>
            private CancellationSourcePool.Use _timeoutTrigger;

            internal enum CallbackStage : int
            {
                Start = 0,
                Completed = 1,
                TimedOut = 2,
                Cancelled = 3
            }

            private int _callbackStage;

            /// <summary>
            /// Freshly check if the parent promise has published, or if subscriber-specific
            /// cancellation has occurred.
            /// </summary>
            /// <remarks>
            /// <para>
            /// If the promise has published its result already, that takes precedence
            /// over cancellation.
            /// </para>
            /// <para>
            /// This method only returns the result of the poll and does not set it into
            /// <see cref="_callbackStage"/>, since that might require coordination
            /// with consequent operations.
            /// </para>
            /// </remarks>
            private CallbackStage Poll()
                => Promise.IsCompleted ? CallbackStage.Completed :
                   CancellationToken.IsCancellationRequested ? CallbackStage.Cancelled :
                   _timeoutTrigger.Token.IsCancellationRequested ? CallbackStage.TimedOut :
                   CallbackStage.Start;

            /// <summary>
            /// True when this node has been queued under <see cref="Promise._firstToWake"/>;
            /// otherwise false.
            /// </summary>
            private bool _inWakeUpList;

            /// <summary>
            /// True when this node is attached to the parent promise, i.e. is 
            /// managing an active subscription. 
            /// </summary>
            private bool _isAttached;
        }

        /// <summary>
        /// Points to the first of subscription entries attached to this promise
        /// that are not waiting for something to be published.
        /// </summary>
        private SubscriptionNode? _firstSubscription;

        /// <summary>
        /// Points to the first of subscription entries attached to this promise
        /// that are waiting for <see cref="SubscribedResult"/> to be published.
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
        /// Subscribes to this promise and prepare to await/retrieve its results.
        /// </summary>
        public ValueTask<SubscribedResult> GetResultAsync(IPromiseClientInfo client, 
                                                          in TimeSpan? timeout, 
                                                          CancellationToken cancellationToken)
            => SubscriptionNode.CreateValueTask(this, client, timeout, cancellationToken);
    }
}
