using System;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// A job queue, for the fair scheduling system, backed by 
    /// <see cref="ChannelReader{T}" />.
    /// </summary>
    public class SimpleJobQueue<TJob> : SchedulingUnit<TJob>
    {
        private readonly ChannelReader<TJob> _channelReader;

        public SimpleJobQueue(SchedulingGroup<TJob> parent, 
                              ChannelReader<TJob> channelReader,
                              int weight = 1)
            : base(parent, weight)
        {
            _channelReader = channelReader;
        }

        protected override TJob? TakeJob(out int charge)
        {
            charge = 0;
            if (_channelReader.TryRead(out var job))
                return job;

            _ = ActivateWhenReadyAsync();
            return default;
        }

        private async Task ActivateWhenReadyAsync()
        {
            bool hasMore = await _channelReader.WaitToReadAsync()
                                               .ConfigureAwait(false);
            if (hasMore)
                Activate();
        }
    }
}
