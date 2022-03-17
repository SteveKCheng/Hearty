using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Scheduling
{
    /// <summary>
    /// A scheduling flow backed by <see cref="ConcurrentQueue{T}" />.
    /// </summary>
    /// <typeparam name="T">
    /// The type of items that are put into the queue.
    /// </typeparam>
    /// <typeparam name="TOut">
    /// The type of items that come out from the queue
    /// after transformation.
    /// </typeparam>
    /// <remarks>
    /// <para>
    /// The contents of the queue can be observed, useful to visualize
    /// (job) queues.  This functionality is not possible using an
    /// abstract <see cref="System.Threading.Channels.ChannelReader{T}" />,
    /// even though internally the implementation from 
    /// <see cref="System.Threading.Channels.Channel.CreateUnbounded{T}()"/>
    /// internally based on <see cref="ConcurrentQueue{T}" />.
    /// </para>
    /// <para>
    /// Use <see cref="SchedulingChannel{T}" /> instead of this class 
    /// to adapt an existing channel as a scheduling flow.
    /// </para>
    /// <para>
    /// This scheduling flow has the advanced feature of allowing entries
    /// of type <see cref="IAsyncEnumerable{T}" /> to be placed in 
    /// the queue also.  When such an entry is de-queued, they may
    /// expand asynchronously to a sequence of items of type
    /// <typeparamref name="T"/>, which are considered
    /// to cut in front of the queue.  
    /// </para>
    /// <para>
    /// This feature can be used in job scheduling to push in a "macro job" 
    /// which dynamically expand into hundreds or even thousands of "micro jobs", 
    /// without having to materialize all of them up front to put into the queue.
    /// Certain applications may be able to represent the "micro jobs" a 
    /// more efficient manner than a straight in-memory sequence of 
    /// <typeparamref name="T" />.
    /// </para>
    /// <para>
    /// This transformation must be implemented at the level of a scheduling 
    /// flow, and not on top of the scheduling group, because the items 
    /// in the sub-sequence need to stay in the same scheduling flow
    /// to be prioritized correctly.
    /// </para>
    /// <para>
    /// Finally, individual items are subject to a transformation that
    /// can be arbitrarily specified.  The transformation is typically
    /// used to stamp the items 
    /// so the originating scheduling flow can be identified when
    /// the items come out of the containing scheduling group.
    /// </para>
    /// </remarks>
    public class SchedulingQueue<T, TOut> 
        : SchedulingFlow<TOut>
        , ISchedulingFlow<TOut>
        , ISchedulingAccount
        , IReadOnlyCollection<AsyncExpandable<T>>
        where T: ISchedulingExpense
    {
        /// <summary>
        /// Holds items and sub-sequences that are dequeued
        /// from the front and enqueued at the back.
        /// </summary>
        private readonly ConcurrentQueue<AsyncExpandable<T>> _queue;

        /// <summary>
        /// Function to transform the items put into the queue
        /// to what comes out of the scheduling flow.
        /// </summary>
        private readonly Func<T, TOut> _itemTransform;

        /// <summary>
        /// Current count of items, to optimize activation/de-activation of this queue.
        /// </summary>
        private int _count = 0;

        /// <inheritdoc cref="IReadOnlyCollection{T}.Count" />
        public int Count => _count;

        /// <inheritdoc cref="ISchedulingFlow{T}.AsFlow" />
        public SchedulingFlow<TOut> AsFlow() => this;

        /// <summary>
        /// Construct the queue as initially empty.
        /// </summary>
        /// <param name="itemTransform">
        /// Function to transform the items put into the queue
        /// to what comes out of the scheduling flow.
        /// </param>
        public SchedulingQueue(Func<T, TOut> itemTransform)
        {
            _itemTransform = itemTransform ?? throw new ArgumentNullException(nameof(itemTransform));
            _queue = new ConcurrentQueue<AsyncExpandable<T>>();
        }

        #region Expanding entries that are sub-sequences

        /// <summary>
        /// The enumerator, if any, that should be consulted first
        /// before any items in <see cref="_queue" />.
        /// </summary>
        private IAsyncEnumerator<T>? _currentEnumerator;

        /// <summary>
        /// The saved return value of <see cref="IAsyncEnumerator{T}.MoveNextAsync" />,
        /// for retrieving the next item from <see cref="_currentEnumerator" />.
        /// </summary>
        private ValueTask<bool> _currentEnumeratorTask;

        /// <summary>
        /// Set to true if <see cref="_currentEnumeratorTask" /> is being awaited,
        /// to prevent multiple awaiters.
        /// </summary>
        private bool _isWaitingOnEnumerator;

        /// <summary>
        /// Object that is to be locked to defend against improper concurrent
        /// calls to <see cref="TryTakeItem" />.
        /// </summary>
        /// <remarks>
        /// <see cref="TryTakeItem" /> may manipulate the variables relating
        /// to the enumerators, which do not tolerate races. 
        /// That method should not ever be called concurrently
        /// by <see cref="SchedulingGroup{T}" />, but nevertheless
        /// we make the implementation robust.  This critical section
        /// is taken when reading from the scheduling flow only.
        /// </remarks>
        private object ReadSideCriticalSection => _queue;

        /// <summary>
        /// Check for a next item that may be available from the 
        /// current enumerator, which is always 
        /// considered to "cut in of the queue".
        /// </summary>
        /// <param name="item">
        /// If there is a next item from the enumerator, it
        /// will be set here on return.
        /// </param>
        /// <param name="charge">
        /// The initial charge for the item.
        /// </param>
        /// <param name="waiting">
        /// If true, this scheduling flow needs to asynchronously
        /// wait for the enumerator to produce the next item.
        /// If false, either there is a next item immediately
        /// available from the enumerator or the enumerator 
        /// has been exhausted.
        /// </param>
        /// <returns>
        /// Whether there is a next item immediately available
        /// from the enumerator.
        /// </returns>
        /// <remarks>
        /// When <paramref name="waiting" /> is false on return,
        /// this scheduling flow is de-activated, until 
        /// <see cref="IAsyncEnumerator{T}.MoveNextAsync" /> completes
        /// asynchronously when the flow is activated again.
        /// </remarks>
        private bool TryTakeItemFromEnumerator(
            [MaybeNullWhen(false)] out TOut item, 
            out int charge,
            out bool waiting)
        {
            item = default;
            charge = default;
            waiting = false;

            var enumerator = _currentEnumerator;
            if (enumerator is null)
                return false;

            var task = _currentEnumeratorTask;
            if (task.IsCompleted)
            {
                _isWaitingOnEnumerator = false;

                if (task.IsCompletedSuccessfully && task.Result == true)
                {
                    var input = enumerator.Current;
                    item = _itemTransform(input);
                    charge = input.GetInitialCharge();
                    _currentEnumeratorTask = SafelyAdvanceAsyncEnumerator(enumerator);
                    return true;
                }
                else
                {
                    _currentEnumerator = null;
                    _currentEnumeratorTask = default;
                    _ = enumerator.FireAndForgetDisposeAsync();
                    DeactivateOnEmptyQueue();
                }
            }
            else
            {
                waiting = true;
                Deactivate();
                if (!_isWaitingOnEnumerator)
                {
                    _isWaitingOnEnumerator = true;
                    _ = ActivateOnReadyEnumeratorAsync();
                }
            }

            return false;
        }

        /// <summary>
        /// Continuation on <see cref="IAsyncEnumerator{T}.MoveNextAsync" />
        /// that asynchronously completes to re-activate this scheduling flow.
        /// </summary>
        private async Task ActivateOnReadyEnumeratorAsync()
        {
            try
            {
                await _currentEnumeratorTask.ConfigureAwait(false);
            }
            catch 
            {
            }

            Activate();
        }

        /// <summary>
        /// Invoke <see cref="IAsyncEnumerator{T}.MoveNextAsync" />,
        /// treating any thrown exception as if it returned false.
        /// </summary>
        private static ValueTask<bool> SafelyAdvanceAsyncEnumerator(IAsyncEnumerator<T> enumerator)
        {
            try
            {
                return enumerator.MoveNextAsync();
            }
            catch
            {
                return ValueTask.FromResult(false);
            }
        }

        #endregion

        /// <summary>
        /// Decrement <see cref="_count" /> after an entry has been successfully
        /// processed out of it, de-activating the queue if there are no
        /// more entries.
        /// </summary>
        /// <remarks>
        /// When an entry expands to multiple items via <see cref="IAsyncEnumerator{T}" />,
        /// the "master" entry is not considered processed,
        /// and thus <see cref="_count" /> is not decremented, 
        /// until the last of the expanded items has been seen. 
        /// So enqueuing new entries (from <see cref="Enqueue(T)" />) 
        /// while an enumerator is active do not re-activate the scheduling flow.
        /// </remarks>
        private void DeactivateOnEmptyQueue()
        {
            if (Interlocked.Decrement(ref _count) == 0)
            {
                // In the common case of emptying the queue, when there is no race,
                // de-activate the queue immediately so SchedulingGroup does not
                // poll it again.
                Deactivate();

                // If Deactivate above raced with Activate in Enqueue,
                // then Activate again to be safe.
                if (_count > 0)
                    Activate();
            }
        }

        /// <inheritdoc />
        protected sealed override bool TryTakeItem([MaybeNullWhen(false)] out TOut item, out int charge)
        {
            lock (ReadSideCriticalSection)
            {
                AsyncExpandable<T> entry;

                while (true)
                {
                    if (TryTakeItemFromEnumerator(out item, out charge, out bool waiting))
                        return true;

                    if (waiting)
                    {
                        Deactivate(temporary: true);
                        return false;
                    }

                    if (!_queue.TryDequeue(out entry))
                        return false;

                    var enumerable = entry.Multiple;
                    if (enumerable is null)
                        break;

                    var enumerator = enumerable.GetAsyncEnumerator();
                    _currentEnumerator = enumerator;
                    _currentEnumeratorTask = SafelyAdvanceAsyncEnumerator(enumerator);
                }

                var input = entry.Single;
                item = _itemTransform(input);
                charge = input.GetInitialCharge();

                DeactivateOnEmptyQueue();

                return true;
            }
        }

        /// <summary>
        /// Push an entry (which may in turn expand to a sub-sequence) 
        /// to the end of the queue.
        /// </summary>
        /// <remarks>
        /// The queue if activated as a scheduling flow 
        /// if it is not already activated.
        /// </remarks>
        /// <param name="entry">The entry to push to the queue. </param>
        public void Enqueue(AsyncExpandable<T> entry)
        {
            // Increment first to avoid negative counts when this method
            // races with TryTakeItem.
            bool toActivate = (Interlocked.Increment(ref _count) <= 1);

            _queue.Enqueue(entry);

            // Activate takes locks, which we can avoid most of the time.
            if (toActivate)
                Activate();
        }

        /// <summary>
        /// Push one item to the end of the queue.
        /// </summary>
        /// <remarks>
        /// The queue if activated as a scheduling flow 
        /// if it is not already activated.
        /// </remarks>
        /// <param name="item">The item to push to the queue. </param>
        public void Enqueue(T item) 
            => Enqueue(new AsyncExpandable<T>(item));

        /// <summary>
        /// Push a sub-sequence of items to the end of the queue.
        /// </summary>
        /// <remarks>
        /// The queue if activated as a scheduling flow 
        /// if it is not already activated.
        /// </remarks>
        /// <param name="sequence">The sub-sequence to push to the queue. </param>
        public void Enqueue(IAsyncEnumerable<T> sequence) 
            => Enqueue(new AsyncExpandable<T>(sequence));

        /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />
        public IEnumerator<AsyncExpandable<T>> GetEnumerator() => _queue.GetEnumerator();

        /// <inheritdoc cref="IEnumerable.GetEnumerator" />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc cref="ISchedulingAccount.UpdateCurrentItem" />
        void ISchedulingAccount.UpdateCurrentItem(int? current, int change)
        {
            if (current != null)
                base.AdjustBalance(change != int.MinValue ? -change : int.MaxValue);
        }

        #region Statistics

        private SequenceLock<SchedulingStatistics> _completionStats;

        /// <inheritdoc cref="ISchedulingAccount.TabulateCompletedItem(int)" />
        public void TabulateCompletedItem(int charge)
        {
            var oldStats = _completionStats.BeginWriteTransaction(out uint version);
            _completionStats.EndWriteTransaction(version, new SchedulingStatistics
            {
                CumulativeCharge = oldStats.CumulativeCharge + charge,
                ItemsCount = oldStats.ItemsCount + 1
            });
        }

        /// <inheritdoc cref="ISchedulingAccount.CompletionStatistics" />
        public SchedulingStatistics CompletionStatistics => _completionStats.Read();

        #endregion
    }

    /// <summary>
    /// Same as <see cref="SchedulingQueue{T, TOut}" />
    /// without any item transformation. 
    /// </summary>
    /// <typeparam name="T">
    /// The type of items that are put into the queue.
    /// </typeparam>
    public class SchedulingQueue<T> : SchedulingQueue<T,T>
        where T : ISchedulingExpense
    {
        /// <summary>
        /// The identity function to do no transformation of
        /// the input items.
        /// </summary>
        private static readonly Func<T, T> _identityTransform = x => x;

        /// <summary>
        /// Construct the queue as initially empty.
        /// </summary>
        public SchedulingQueue()
            : base(_identityTransform)
        {
        }
    }
}
