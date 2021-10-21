using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// A job queue, for the fair scheduling system, backed by 
    /// <see cref="ChannelReader{T}" />.
    /// </summary>
    public class SimpleJobQueue<T> : SchedulingUnit<T> where T: ISchedulingExpense
    {
        private readonly ChannelReader<T> _channelReader;

        public SimpleJobQueue(ChannelReader<T> channelReader)
        {
            _channelReader = channelReader;
        }

        protected override bool TryTakeItem(
            [MaybeNullWhen(false)] out T item, out int charge)
        {
            if (_channelReader.TryRead(out item))
            {
                charge = item.InitialCharge;
                return true;
            }
                
            charge = 0;
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
