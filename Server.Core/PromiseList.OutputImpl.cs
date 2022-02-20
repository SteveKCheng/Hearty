using JobBank.Common;
using System;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server;

public partial class PromiseList
{
    /// <summary>
    /// Set of methods passed into <see cref="GenerateIntoPipeAsync" />
    /// to specialize for each format supported by <see cref="PromiseList" />.
    /// </summary>
    private abstract class OutputImpl
    {
        /// <summary>
        /// Write the next item into the pipe.
        /// </summary>
        /// <param name="self">The instance of <see cref="PromiseList" />,
        /// passed in so that <see cref="OutputImpl" /> can be a singleton.
        /// </param>
        /// <param name="writer">
        /// Where the output goes.
        /// </param>
        /// <param name="ordinal">
        /// The index of the promise in the original input ordering.
        /// </param>
        /// <param name="promiseId">
        /// Promise ID of the next item.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to interrupt writing.
        /// </param>
        /// <returns>
        /// The number of unflushed bytes written by this method,
        /// or -1 if it flushes.
        /// </returns>
        public abstract ValueTask<int> WriteItemAsync(PromiseList self,
                                                      PipeWriter writer,
                                                      int ordinal,
                                                      PromiseId promiseId,
                                                      CancellationToken cancellationToken);

        /// <summary>
        /// Write the prologue after all the items have been written.
        /// </summary>
        /// <param name="self">The instance of <see cref="PromiseList" />,
        /// passed in so that <see cref="OutputImpl" /> can be a singleton.
        /// </param>
        /// <param name="writer">
        /// Where the output goes.
        /// </param>
        /// <param name="exception">
        /// The exception, if any, that the promise list has been
        /// completed with.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to interrupt writing.
        /// </param>
        /// <returns>
        /// The number of unflushed bytes written by this method,
        /// or -1 if it flushes.
        /// </returns>
        public abstract ValueTask<int> WriteEndAsync(PromiseList self,
                                                     PipeWriter writer,
                                                     Exception? exception,
                                                     CancellationToken cancellationToken);
    }

    /// <summary>
    /// Hooks to write out <see cref="PromiseList" /> as a plain-text stream.
    /// </summary>
    private sealed class PlainTextImpl : OutputImpl
    {
        public static readonly PlainTextImpl Instance = new();

        public override ValueTask<int> WriteEndAsync(PromiseList self,
                                                     PipeWriter writer,
                                                     Exception? exception,
                                                     CancellationToken cancellationToken)
        {
            int numBytes = 0;

            if (exception is not null)
            {
                bool isCancellation = exception is OperationCanceledException;
                string text = isCancellation ? "<CANCELLED>\r\n" : "<FAILED>\r\n";
                numBytes = (int)writer.WriteUtf8String(text);
            }

            return ValueTask.FromResult(numBytes);
        }

        public override ValueTask<int> WriteItemAsync(PromiseList self,
                                                      PipeWriter writer,
                                                      int ordinal,
                                                      PromiseId promiseId,
                                                      CancellationToken cancellationToken)
        {
            var buffer = writer.GetMemory(PromiseId.MaxChars + 2);

            int numBytes = promiseId.FormatAscii(buffer);

            // Terminate each entry by Internet-standard new-line
            numBytes += AppendNewLine(buffer.Span[numBytes..]);

            writer.Advance(numBytes);
            return ValueTask.FromResult(numBytes);
        }
    }

    private class DummyPromiseClientInfo : IPromiseClientInfo
    {
        public string UserName => "current-user";

        public ClaimsPrincipal? User => null;

        public uint OnSubscribe(Subscription subscription)
        {
            return 0;
        }

        public void OnUnsubscribe(Subscription subscription, uint index)
        {
        }
    }

    internal static readonly IPromiseClientInfo dummyClient = new DummyPromiseClientInfo();

    /// <summary>
    /// Hooks to write out <see cref="PromiseList" /> as a multi-part stream.
    /// </summary>
    private sealed class MultiPartImpl : OutputImpl
    {
        public static readonly MultiPartImpl Instance = new();

        public override ValueTask<int> WriteEndAsync(PromiseList self,
                                                     PipeWriter writer,
                                                     Exception? exception,
                                                     CancellationToken cancellationToken)
        {
            int numBytes = WriteBoundary(writer, isEnd: true);

            if (exception is not null)
            {
                bool isCancellation = exception is OperationCanceledException;
                string text = isCancellation ? "<CANCELLED>\r\n" : "<FAILED>\r\n";
                numBytes += (int)writer.WriteUtf8String(text);
            }

            return ValueTask.FromResult(numBytes);
        }

        // Write <CRLF> "--!" <CRLF>
        private static int WriteBoundary(PipeWriter writer, bool isEnd)
        {
            var span = writer.GetSpan(9);
            int numBytes = AppendNewLine(span);
            span[2] = (byte)'-';
            span[3] = (byte)'-';
            span[4] = BoundaryChar;
            numBytes += 3;
            if (isEnd)
            {
                span[5] = (byte)'-';
                span[6] = (byte)'-';
                numBytes += 2;
            }
            
            numBytes += AppendNewLine(span[numBytes..]);
            writer.Advance(numBytes);
            return numBytes;
        }

        public override async ValueTask<int> WriteItemAsync(PromiseList self,
                                                            PipeWriter writer,
                                                            int ordinal,
                                                            PromiseId promiseId,
                                                            CancellationToken cancellationToken)
        {
            WriteBoundary(writer, isEnd: false);

            var promise = self._promiseStorage.GetPromiseById(promiseId);
            if (promise is null)
                throw new InvalidOperationException($"Promise with ID {promiseId} does not exist even though it has been put as part of the results. ");

            var result = await promise.GetResultAsync(dummyClient,
                                                      null, cancellationToken)
                                      .ConfigureAwait(false);
            var output = result.NormalOutput;

            // RFC 2046 says that Content-Transfer-Encoding is "7bit" by default!
            // However, HTTP clients have to be prepared to receive and send
            // arbitrary binary data, and in practice most other servers do not
            // bother sending this header.  It just adds inefficiency.
            //
            //   writer.WriteUtf8String("Content-Transfer-Encoding: Binary\r\n");

            writer.WriteUtf8String(JobBankHttpHeaders.Ordinal);
            writer.WriteUtf8String(": ");
            writer.WriteDecimalInteger(ordinal);
            writer.WriteCrLf();

            writer.WriteUtf8String(JobBankHttpHeaders.PromiseId);
            writer.WriteUtf8String(": ");
            writer.WriteAsciiPromiseId(promiseId);
            writer.WriteCrLf();

            writer.WriteUtf8String("Content-Type: ");
            writer.WriteUtf8String(output.ContentType);
            writer.WriteCrLf();

            writer.WriteUtf8String("Content-Length: ");

            if (output.ContentLength is long length)
            {
                writer.WriteDecimalInteger(length);
                writer.WriteCrLf();
                writer.WriteCrLf();
                await output.WriteToPipeAsync(0, writer, 0, cancellationToken)
                            .ConfigureAwait(false);
            }
            else
            {
                var payload = await output.GetPayloadAsync(0, cancellationToken)
                                          .ConfigureAwait(false);
                writer.WriteDecimalInteger(payload.Length);
                writer.WriteCrLf();
                writer.WriteCrLf();
                await writer.WriteAsync(payload, cancellationToken)
                            .ConfigureAwait(false);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return -1;
        }

        private const byte BoundaryChar = (byte)'#';
    }

    private static int AppendNewLine(Span<byte> line)
    {
        line[0] = (byte)'\r';
        line[1] = (byte)'\n';
        return 2;
    }
}
