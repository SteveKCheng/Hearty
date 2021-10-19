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
                cancellationToken.ThrowIfCancellationRequested();

                if (_subgroup.CountActiveSources > 0)
                    return new ValueTask<bool>(true);

                short version;

                lock (_taskSource)
                {
                    if (_acceptsActivation != 0)
                        throw new InvalidOperationException("Cannot call WaitToReadAsync while another asynchronous request is active. ");

                    _taskSource.Reset();
                    version = _taskSource.Version;

                    // Acquire+Release in C++11 memory model.
                    //
                    // Release ordering is to make sure _taskSource is committed.
                    // Acquire ordering is to make sure that _acceptsActivation = 1
                    // is visible to the thread calling OnSubgroupActivated()
                    // before we go on to double-check CountActiveSources > 0 below.
                    Interlocked.Exchange(ref _acceptsActivation, 1);

                    // Something might have been enqueued after the first check
                    // but before _acceptsActivation was set to 1.
                    if (_subgroup.CountActiveSources > 0)
                    {
                        _acceptsActivation = 0;
                        return new ValueTask<bool>(true);
                    }

                    _cancellationRegistration = cancellationToken.Register(s => {
                        var self = Unsafe.As<ChannelReader>(s!);

                        // N.B. could be a recursive lock because CancellationToken.Register
                        //      may invoke this callback synchronously.
                        lock (self._taskSource) 
                        {
                            self._taskSource.TrySetException(new OperationCanceledException());
                        }
                    }, this);
                }

                return new ValueTask<bool>(_taskSource, version);
            }

            internal void OnSubgroupActivated()
            {
                if (Interlocked.CompareExchange(ref _acceptsActivation, -1, 1) == 1)
                {
                    lock (_taskSource)
                    {
                        _taskSource.TrySetResult(true);
                        _cancellationRegistration.Dispose();
                        _acceptsActivation = 0;
                    }
                }
            }

            private int _acceptsActivation = 0;
            private CancellationTokenRegistration _cancellationRegistration;

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

