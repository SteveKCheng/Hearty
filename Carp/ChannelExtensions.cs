using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Hearty.Carp;

/// <summary>
/// Extension methods on <see cref="Channel" /> and related classes.
/// </summary>
internal static class ChannelExtensions
{
    /// <summary>
    /// Asynchronously write to a channel, unless
    /// the channel is closed and cannot be written to again.
    /// </summary>
    /// <typeparam name="T">The type of item in the channel. </typeparam>
    /// <param name="writer">
    /// Where the item is to be written to.
    /// </param>
    /// <param name="item">
    /// The item to attempt to write to the channel.
    /// </param>
    /// <param name="cancellationToken">
    /// Can be used to cancel the writing of the item.
    /// </param>
    /// <returns>
    /// The asynchronous result is true once the item
    /// has been successfully written, or false if the channel
    /// has been closed.
    /// </returns>
    /// <remarks>
    /// This method differs from <see cref="ChannelWriter{T}.WriteAsync" />
    /// only in that it does not throw an exception if the channel is closed,
    /// but reports that condition in the return value.
    /// </remarks>
    public static async ValueTask<bool> TryWriteAsync<T>(this ChannelWriter<T> writer, 
                                                         T item,
                                                         CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        do
        {
            if (writer.TryWrite(item))
                return true;
        } while (await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false));

        return false;
    }
}
