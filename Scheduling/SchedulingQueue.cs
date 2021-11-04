using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

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
    /// Use <see cref="SimpleJobQueue{T}" /> instead of this class 
    /// to adapt an existing channel as a scheduling flow.
    /// </para>
    /// </remarks>
    public sealed class SchedulingQueue<T> 
        : SchedulingFlow<T>, ISchedulingFlow<T>, IReadOnlyCollection<T>
        where T: ISchedulingExpense
    {
        private readonly ConcurrentQueue<T> _queue;

        /// <inheritdoc cref="IReadOnlyCollection{T}.Count" />
        public int Count => _queue.Count;

        /// <inheritdoc cref="ISchedulingFlow{T}.AsFlow" />
        public SchedulingFlow<T> AsFlow() => this;

        /// <summary>
        /// Construct the queue as initially empty.
        /// </summary>
        public SchedulingQueue()
        {
            _queue = new ConcurrentQueue<T>();
        }

        /// <inheritdoc />
        protected override bool TryTakeItem([MaybeNullWhen(false)] out T item, out int charge)
        {
            bool success = _queue.TryDequeue(out item);
            charge = success ? item!.InitialCharge : default;
            return success;
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
            _queue.Enqueue(item);
            Activate();
        }

        /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />
        public IEnumerator<T> GetEnumerator() => _queue.GetEnumerator();

        /// <inheritdoc cref="IEnumerable.GetEnumerator" />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
