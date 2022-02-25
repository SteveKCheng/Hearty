using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Hearty.Scheduling
{
    public partial class SchedulingGroup<T>
    {
        /// <summary>
        /// Allows items from <see cref="SchedulingGroup{T}" /> 
        /// to be read through the standard "channels" interface.
        /// </summary>
        public sealed class ChannelReader : ChannelReader<T>
        {
            /// <summary>
            /// The scheduling group that is being adapted to be a channel.
            /// </summary>
            private readonly SchedulingGroup<T> _subgroup;

            internal ChannelReader(SchedulingGroup<T> subgroup)
            {
                _subgroup = subgroup;
            }

            /// <inheritdoc />
            public override bool TryRead([MaybeNullWhen(false)] out T item)
            {
                if (_isFinished)
                {
                    item = default;
                    return false;
                }
                    
                return _subgroup.TryTakeItem(out item, out _);
            }

            /// <inheritdoc />
            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Return if there is any item in the queue, unless the channel
                // has been marked completed.  Note that requests to complete
                // are actioned only after all items in the queue are drained,
                // so that test comes last.  But if the channel has already been
                // completed, the first condition ensures no items are returned
                // even if they got added later to the scheduling group.  That
                // is completion status "sticks", as required by ChannelReader's
                // contract.
                bool toFinish;
                if ((toFinish = _isFinished) ||
                    _subgroup.CountActiveSources > 0 || 
                    (toFinish = FinishIfRequested()))
                {
                    return new ValueTask<bool>(!toFinish);
                }

                var task = _taskSource.Reset(cancellationToken);

                // Something might have been enqueued after the first check
                // but before the state on _taskSource was transitioned.
                //
                // If a request to complete races with new items being added, 
                // consider the termination to "win", since we can reach here
                // only if the queue has already been drained.
                if ((toFinish = FinishIfRequested()) || 
                    _subgroup.CountActiveSources > 0)
                {
                    // Make sure the _taskSource is completed so it can be reset next
                    // time this method is called.  As completing _taskSource can 
                    // race, we must return a reference to it and not the direct
                    // result (!toTerminate) to this method's caller.
                    _taskSource.TrySetResult(!toFinish);
                }

                return task;
            }

            /// <summary>
            /// Called by the scheduling group that this channel represents
            /// when it may have items available, so that <see cref="WaitToReadAsync" />
            /// may return asynchronously.
            /// </summary>
            internal void OnSubgroupActivated() => _taskSource.TrySetResult(true);

            /// <summary>
            /// Polls if this instance has been requested to complete,
            /// and if so, calls <see cref="Finish" />.
            /// </summary>
            /// <returns>
            /// Whether this instance has been requested to complete.
            /// </returns>
            private bool FinishIfRequested()
            {
                if (!_requestedToFinish)
                    return false;

                Finish();
                return true;
            }

            /// <summary>
            /// Mark the channel as having completed.
            /// </summary>
            /// <remarks>
            /// <para>
            /// If there are still items from the scheduling group, they 
            /// will not ever be returned through this <see cref="ChannelReader" />.
            /// </para>
            /// <para>
            /// This method signals completion through <see cref="Completion" />,
            /// but not through <see cref="_taskSource" /> which is handled by
            /// other methods.  This method effectively does not do anything
            /// if it has already been called: i.e. the first caller "wins".
            /// </para>
            /// </remarks>
            private void Finish()
            {
                AsyncTaskMethodBuilder completionTaskBuilder = default;
                lock (_taskSource)
                {
                    if (!_isFinished)
                    {
                        completionTaskBuilder = _completionTaskBuilder;
                        _completionTaskBuilder = default;
                    }

                    _isFinished = true;
                }

                // Invoke any continuations on the task from the Completion
                // property, outside the lock.  This statement effectively
                // does nothing if the Completion property has never been
                // accessed before.
                completionTaskBuilder.SetResult();
            }

            /// <summary>
            /// Request completion of the channel as described in
            /// <see cref="SchedulingGroup{T}.TerminateChannelReader" />.
            /// </summary>
            internal void RequestToFinish()
            {
                _requestedToFinish = true;

                // If this succeeds, that means the reader was waiting for
                // this channel and all items have been drained.
                if (_taskSource.TrySetResult(false))
                    Finish();
            }

            /// <inheritdoc />
            public override Task Completion
            {
                get
                {
                    lock (_taskSource)
                    {
                        if (_isFinished)
                            return Task.CompletedTask;

                        return _completionTaskBuilder.Task;
                    }
                }
            }

            /// <summary>
            /// Roundabout way to create and set the <see cref="Task" /> object
            /// to implement <see cref="Completion" />.
            /// </summary>
            /// <remarks>
            /// This structure is not thread-safe by itself; we mediate
            /// access by locking <see cref="_taskSource" />.
            /// </remarks>
            private AsyncTaskMethodBuilder _completionTaskBuilder;

            /// <summary>
            /// Set to true when the channel has been marked complete.
            /// </summary>
            private bool _isFinished;

            /// <summary>
            /// Set to true if this channel has been requested to complete.
            /// </summary>
            private bool _requestedToFinish;

            /// <summary>
            /// Task source to implement <see cref="WaitToReadAsync" />.
            /// </summary>
            private readonly ResettableConcurrentTaskSource<bool> _taskSource = new();
        }

        /// <summary>
        /// Obtains a <see cref="ChannelReader{T}" /> that consumes items
        /// scheduled by this scheduling group.
        /// </summary>
        /// <returns>
        /// An instance of <see cref="ChannelReader{T}" /> connected
        /// to this scheduling group.
        /// </returns>
        public ChannelReader AsChannelReader()
        {
            var toWakeUp = _toWakeUp;
            if (toWakeUp == null)
            {
                var newChannelReader = new ChannelReader(this);
                toWakeUp = Interlocked.CompareExchange(ref _toWakeUp,
                                                       newChannelReader,
                                                       null);
                if (toWakeUp == null)
                    return newChannelReader;
            }

            if (toWakeUp is not ChannelReader channelReader)
            {
                throw new InvalidOperationException(
                    "Cannot call AsChannelReader when AsSource had already been called. ");
            }

            return channelReader;
        }

        /// <summary>
        /// Completes the channel returned by 
        /// <see cref="AsChannelReader" /> as soon
        /// as all remaining items have been drained.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method can be called when the application wants
        /// to gracefully stop processing a queue system.  First,
        /// the application has to stop each of 
        /// <see cref="SchedulingFlow{T}"/> instances
        /// that feed into this queue system, meaning that no new
        /// items can be enqueued into them.  Then this method 
        /// is called. 
        /// </para>
        /// <para>
        /// The loop that consumes items from 
        /// <see cref="AsChannelReader" /> shall continue
        /// to do so to process the items still remaining
        /// in the system, or raced to get into the system before
        /// the child flows have been stopped.  This method ensures
        /// those items are not lost, and signals the channel
        /// is complete after those items are read.
        /// </para>
        /// </remarks>
        public void TerminateChannelReader()
        {
            if (_toWakeUp is not ChannelReader channelReader)
            {
                throw new InvalidOperationException(
                    "Cannot call TerminateChannelReader since no channel reader had been requested earlier. ");
            }

            channelReader.RequestToFinish();
        }
    }
}
