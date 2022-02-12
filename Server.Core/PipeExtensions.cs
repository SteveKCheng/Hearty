using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;
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

        /// <summary>
        /// Write text in UTF-8 encoding.
        /// </summary>
        /// <param name="destination">The output destination. </param>
        /// <param name="text">The text to write.  It should
        /// be short, as there is no way this method can
        /// flush buffers to re-use them. </param>
        /// <returns>
        /// The number of bytes written.
        /// </returns>
        public static long WriteUtf8String(this IBufferWriter<byte> destination, string text)
            => Encoding.UTF8.GetBytes(text, destination);

        /// <summary>
        /// Write the CR-LF (Carriage Return; Line Feed) sequence
        /// in ASCII.
        /// </summary>
        /// <param name="destination">The output destination. </param>
        /// <returns>
        /// The number of bytes written, which is 2.
        /// </returns>
        public static int WriteCrLf(this IBufferWriter<byte> writer)
        {
            var span = writer.GetSpan(2);
            span[0] = (byte)'\r';
            span[1] = (byte)'\n';
            writer.Advance(2);
            return 2;
        }

        /// <summary>
        /// Write out an integer in decimal, using ASCII digits, with
        /// no separator characters.
        /// </summary>
        /// <param name="destination">The output destination. </param>
        /// <returns>
        /// The number of bytes written.
        /// </returns>
        public static int WriteDecimalInteger(this IBufferWriter<byte> destination, 
                                              long value)
        {
            var span = destination.GetSpan(28);
            Utf8Formatter.TryFormat(value, span, out int bytesWritten);
            destination.Advance(bytesWritten);
            return bytesWritten;
        }
    }
}
