﻿using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Extension methods for working with pipes (from System.IO.Pipelines).
/// </summary>
public static class PipeExtensions
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
    /// <param name="destination">The output destination. 
    /// It is advanced by the number of bytes written.
    /// </param>
    /// <param name="text">The text to write.  It should
    /// be short, as there is no way this method can
    /// flush buffers to re-use them. </param>
    /// <returns>
    /// The number of bytes written.  
    /// </returns>
    public static long WriteUtf8String(this IBufferWriter<byte> destination, 
                                       ReadOnlySpan<char> text)
        => Encoding.UTF8.GetBytes(text, destination);

    /// <summary>
    /// Write text in ASCII encoding.
    /// </summary>
    /// <param name="destination">The output destination. 
    /// It is advanced by the number of bytes written.
    /// </param>
    /// <param name="text">The text to write.  It should
    /// be short, as there is no way this method can
    /// flush buffers to re-use them. </param>
    /// <returns>
    /// The number of bytes written.  
    /// </returns>
    public static long WriteAsciiString(this IBufferWriter<byte> destination, 
                                        ReadOnlySpan<char> text)
        => Encoding.ASCII.GetBytes(text, destination);

    /// <summary>
    /// Write the CR-LF (Carriage Return; Line Feed) sequence
    /// in ASCII.
    /// </summary>
    /// <param name="destination">The output destination. </param>
    /// <returns>
    /// The number of bytes written, which is 2.
    /// </returns>
    public static int WriteCrLf(this IBufferWriter<byte> destination)
    {
        var span = destination.GetSpan(2);
        span[0] = (byte)'\r';
        span[1] = (byte)'\n';
        destination.Advance(2);
        return 2;
    }

    /// <summary>
    /// Write out an integer in decimal, using ASCII digits, with
    /// no separator characters.
    /// </summary>
    /// <param name="destination">The output destination. </param>
    /// <param name="value">The integer to write. </param>
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

    /// <summary>
    /// Write out an integer in decimal, using ASCII digits, with
    /// no separator characters.
    /// </summary>
    /// <param name="destination">The output destination. </param>
    /// <param name="value">The integer to write. </param>
    /// <returns>
    /// The number of bytes written.
    /// </returns>
    public static int WriteDecimalInteger(this IBufferWriter<byte> destination,
                                          int value)
    {
        var span = destination.GetSpan(16);
        Utf8Formatter.TryFormat(value, span, out int bytesWritten);
        destination.Advance(bytesWritten);
        return bytesWritten;
    }

    /// <summary>
    /// Write out the textual representation of a promise ID in ASCII.
    /// </summary>
    /// <param name="destination">The output destination. </param>
    /// <param name="value">The promise ID to write. </param>
    /// <returns>
    /// The number of bytes written.
    /// </returns>
    public static int WriteAsciiPromiseId(this IBufferWriter<byte> destination,
                                          PromiseId value)
    {
        var span = destination.GetSpan(PromiseId.MaxChars);
        int bytesWritten = value.FormatAscii(span);
        destination.Advance(bytesWritten);
        return bytesWritten;
    }
}
