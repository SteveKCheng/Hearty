using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.Scheduling
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
                if (_isTerminated)
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

                bool isTerminated = false;
                if (_subgroup.CountActiveSources > 0 || (isTerminated = _isTerminated))
                    return new ValueTask<bool>(!isTerminated);

                var task = _taskSource.Reset(cancellationToken);

                // Something might have been enqueued after the first check
                // but before the state on _taskSource was transitioned.
                isTerminated = false;
                if (_subgroup.CountActiveSources > 0 || (isTerminated = _isTerminated))
                {
                    // We need to transition the task source so it can be reset later.
                    _taskSource.TrySetResult(!isTerminated);

                    // But return the result directly as an optimization.
                    return new ValueTask<bool>(!isTerminated);
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
            /// Mark the channel as having completed.
            /// </summary>
            /// <remarks>
            /// If there are still items from the scheduling group, they 
            /// will not ever be returned through this <see cref="ChannelReader" />.
            /// </remarks>
            internal void Terminate()
            {
                AsyncTaskMethodBuilder completionTaskBuilder = default;
                lock (_taskSource)
                {
                    if (!_isTerminated)
                    {
                        completionTaskBuilder = _completionTaskBuilder;
                        _completionTaskBuilder = default;
                    }

                    _isTerminated = true;
                }

                // Sets Task.CompletedTask trivially if its Task property
                // has not been accessed before.
                completionTaskBuilder.SetResult();

                _taskSource.TrySetResult(false);
            }

            /// <inheritdoc />
            public override Task Completion
            {
                get
                {
                    lock (_taskSource)
                    {
                        if (_isTerminated)
                            return Task.CompletedTask;

                        return _completionTaskBuilder.Task;
                    }
                }
            }

            /// <summary>
            /// Roundabout way to create and set the <see cref="Task" /> object
            /// to implement <see cref="Completion" />.
            /// </summary>
            private AsyncTaskMethodBuilder _completionTaskBuilder;

            /// <summary>
            /// Set to true when the channel has been marked complete.
            /// </summary>
            private bool _isTerminated;

            /// <summary>
            /// Task source to implement <see cref="WaitToReadAsync" />.
            /// </summary>
            private readonly ResettableConcurrentTaskSource<bool> _taskSource = new();
        }

        protected ChannelReader AsChannelReader()
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
        /// Mark the <see cref="ChannelReader{T}" /> returned by
        /// <see cref="AsChannelReader" /> as complete.
        /// </summary>
        protected void TerminateChannelReader()
        {
            if (_toWakeUp is not ChannelReader channelReader)
            {
                throw new InvalidOperationException(
                    "Cannot call TerminateChannelReader since no channel reader had been requested earlier. ");
            }

            channelReader.Terminate();
        }
    }
}

