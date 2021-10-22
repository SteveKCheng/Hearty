using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    public partial class SchedulingGroup<T>
    {
        public sealed class ChannelReader : ChannelReader<T>
        {
            private readonly SchedulingGroup<T> _subgroup;

            internal ChannelReader(SchedulingGroup<T> subgroup)
            {
                _subgroup = subgroup;
            }

            public override bool TryRead([MaybeNullWhen(false)] out T item)
            {
                if (_isTerminated)
                {
                    item = default;
                    return false;
                }
                    
                return _subgroup.TryTakeItem(out item, out _);
            }

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
                    _taskSource.TrySetResult(!isTerminated);
                    return new ValueTask<bool>(!isTerminated);
                }

                return task;
            }

            internal void OnSubgroupActivated() => _taskSource.TrySetResult(true);

            internal void Terminate()
            {
                _isTerminated = true;
                _taskSource.TrySetResult(false);
            }

            private bool _isTerminated;
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

