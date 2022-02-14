using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Adapts <see cref="ChannelReader{T}" /> to be a flow for the
    /// fair scheduling system.
    /// </summary>
    public class SchedulingChannel<T> : SchedulingFlow<T> 
        where T: ISchedulingExpense
    {
        private readonly ChannelReader<T> _channelReader;

        public SchedulingChannel(ChannelReader<T> channelReader)
        {
            _channelReader = channelReader;
        }

        protected override bool TryTakeItem(
            [MaybeNullWhen(false)] out T item, out int charge)
        {
            if (_channelReader.TryRead(out item))
            {
                charge = item.GetInitialCharge();
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
