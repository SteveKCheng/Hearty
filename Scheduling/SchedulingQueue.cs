using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace JobBank.Scheduling
{
    /// <summary>
    /// A scheduling flow backed by <see cref="ConcurrentQueue{T}" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contents of the queue can be observed, useful to visualize
    /// (job) queues.  This functionality is not possible using an
    /// abstract <see cref="System.Threading.Channels.ChannelReader{T}" />,
    /// even though internally the implementation from 
    /// <see cref="System.Threading.Channels.Channel.CreateUnbounded{T}"/>
    /// internally based on <see cref="ConcurrentQueue{T}" />.
    /// </para>
    /// <para>
    /// Use <see cref="SchedulingChannel{T}" /> instead of this class 
    /// to adapt an existing channel as a scheduling flow.
    /// </para>
    /// </remarks>
    public sealed class SchedulingQueue<T> 
        : SchedulingFlow<T>, ISchedulingFlow<T>, ISchedulingAccount, IReadOnlyCollection<T>
        where T: ISchedulingExpense
    {
        private readonly ConcurrentQueue<T> _queue;

        /// <inheritdoc cref="IReadOnlyCollection{T}.Count" />
        public int Count => _count;

        /// <inheritdoc cref="ISchedulingFlow{T}.AsFlow" />
        public SchedulingFlow<T> AsFlow() => this;

        /// <summary>
        /// Construct the queue as initially empty.
        /// </summary>
        public SchedulingQueue()
        {
            _queue = new ConcurrentQueue<T>();
        }

        /// <summary>
        /// Current count of items, to optimize activation/de-activation of this queue.
        /// </summary>
        private int _count = 0;

        /// <inheritdoc />
        protected override bool TryTakeItem([MaybeNullWhen(false)] out T item, out int charge)
        {
            bool success = _queue.TryDequeue(out item);
            if (!success)
            {
                charge = default;
                return false;
            }

            charge = item!.InitialCharge;

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

            return true;
        }

        /// <summary>
        /// Push an item to the end of the queue.
        /// </summary>
        /// <remarks>
        /// The queue if activated as a scheduling flow 
        /// if it is not already activated.
        /// </remarks>
        /// <param name="item">The item to push to the queue. </param>
        public void Enqueue(T item)
        {
            // Increment first to avoid negative counts when this method
            // races with TryTakeItem.
            bool toActivate = (Interlocked.Increment(ref _count) <= 1);

            _queue.Enqueue(item);

            // Activate takes locks, which we can avoid most of the time.
            if (toActivate)
                Activate();
        }

        /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />
        public IEnumerator<T> GetEnumerator() => _queue.GetEnumerator();

        /// <inheritdoc cref="IEnumerable.GetEnumerator" />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc cref="ISchedulingAccount.AdjustBalance" />
        void ISchedulingAccount.AdjustBalance(int debit) => base.AdjustBalance(debit);
    }
}
