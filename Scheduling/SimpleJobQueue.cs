using System;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// A job queue, for the fair scheduling system, backed by 
    /// <see cref="ChannelReader{T}" />.
    /// </summary>
    public class SimpleJobQueue<T> : SchedulingUnit<T>
    {
        private readonly ChannelReader<T> _channelReader;

        public SimpleJobQueue(ChannelReader<T> channelReader)
        {
            _channelReader = channelReader;
        }

        protected override bool TryTakeItem(out T item, out int charge)
        {
            charge = 0;
            if (_channelReader.TryRead(out item!))
                return true;

            _ = ActivateWhenReadyAsync();
            return false;
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
