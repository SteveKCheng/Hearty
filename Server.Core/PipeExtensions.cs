using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Extension methods for working with pipes (from System.IO.Pipelines).
    /// </summary>
    internal static class PipeExtensions
    {
        /// <summary>
        /// Write a byte sequence into a pipe.
        /// </summary>
        /// <param name="writer">The pipe to write into. </param>
        /// <param name="payload">The sequence of bytes to write. </param>
        /// <param name="cancellationToken">
        /// Can be used to interrupt the writing.
        /// </param>
        /// <returns>
        /// Asynchronous task that completes when the desired content
        /// has been written to the pipe, though not necessarily flushed.
        /// This method shall not complete the writing end of the pipe
        /// to let the caller to arrange for more bytes to send to
        /// the same pipe.  If an exception occurs it should be propagated
        /// out of this method as any other asynchronous function, and not
        /// captured as pipe completion.  If the pipe is closed from its
        /// reading end, the asynchronous task may complete normally.
        /// </returns>
        public static async ValueTask WriteAsync(this PipeWriter writer, 
                                                 ReadOnlySequence<byte> payload,
                                                 CancellationToken cancellationToken)
        {
            while (payload.Length > 0)
            {
                var segment = payload.First;
                var flushResult = await writer.WriteAsync(segment, cancellationToken)
                                              .ConfigureAwait(false);
                
                if (flushResult.IsCompleted)
                    return;

                payload  = payload.Slice(segment.Length);
            }
        }
    }
}
