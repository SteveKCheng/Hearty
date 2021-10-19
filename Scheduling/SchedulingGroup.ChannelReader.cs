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
                return _subgroup.TryTakeItem(out item, out _);
            }

            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                if (_subgroup.CountActiveSources > 0)
                    return new ValueTask<bool>(true);

                _taskSource.Reset();
                short version = _taskSource.Version;
                Volatile.Write(ref _acceptActivation, 1);
                Interlocked.MemoryBarrier();

                if (_subgroup.CountActiveSources > 0)
                    return new ValueTask<bool>(true);

                return new ValueTask<bool>(_taskSource, version);
            }

            internal void OnSubgroupActivated()
            {
                if (Interlocked.Exchange(ref _acceptActivation, 0) == 1)
                    _taskSource.SetResult(true);
            }

            private int _acceptActivation = 0;

            private readonly ManualResetValueTaskSource<bool> _taskSource
                = new ManualResetValueTaskSource<bool>();
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

            if (toWakeUp is ChannelReader channelReader)
                return channelReader;

            throw new InvalidOperationException(
                "Cannot call AsChannelReader when AsSource had already been called. ");
        }
    }
}

