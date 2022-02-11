using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// The output of a "macro job" which expands to an
    /// asynchronously-produced sequence of other promises.
    /// </summary>
    public class PromiseList : PromiseData, IPromiseListBuilder
    {
        /// <summary>
        /// Factory to instantiate this class for use with 
        /// <see cref="JobSchedulingSystem" />.
        /// </summary>
        public static PromiseListBuilderFactory Factory { get; }
            = _ => new PromiseList();

        private readonly IncrementalAsyncList<PromiseId> _promiseIds = new();

        bool IPromiseListBuilder.IsComplete => _promiseIds.IsComplete;

        bool IPromiseListBuilder.IsCancelled =>
            _promiseIds.IsComplete && _promiseIds.Exception is OperationCanceledException;

        bool IPromiseListBuilder.TryComplete(int count, Exception? exception)
            => _promiseIds.TryComplete(count, exception);

        void IPromiseListBuilder.SetMember(int index, Promise promise)
            => _promiseIds.TrySetMember(index, promise.Id);
        PromiseData IPromiseListBuilder.Output => this;

        /// <inheritdoc />
        public override ValueTask<Stream> GetByteStreamAsync(string contentType, CancellationToken cancellationToken)
        {
            var pipe = new Pipe();
            _ = GenerateListIntoPipeAsync(pipe.Writer, toComplete: true, cancellationToken);
            return ValueTask.FromResult(pipe.Reader.AsStream());
        }

        /// <inheritdoc />
        public override ValueTask<ReadOnlySequence<byte>> GetPayloadAsync(string contentType, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override ValueTask WriteToPipeAsync(string contentType, PipeWriter writer, long position, CancellationToken cancellationToken)
        {
            if (position != 0)
                throw new NotSupportedException();

            var t = GenerateListIntoPipeAsync(writer, toComplete: false, cancellationToken);
            return new ValueTask(t);
        }

        /// <summary>
        /// Asynchronously write the list of promise IDs in plain-text form into a pipe.
        /// </summary>
        private async Task GenerateListIntoPipeAsync(PipeWriter writer, 
                                                     bool toComplete, 
                                                     CancellationToken cancellationToken)
        {
            Exception? completionException = null;

            try
            {
                int index = 0;
                long totalBytes = 0;
                long flushWatermark = 0;

                while (true)
                {
                    int numBytes;
                    PromiseId promiseId;
                    bool isValid;

                    var memberTask = _promiseIds.TryGetMemberAsync(index, 
                                                                   cancellationToken);

                    try
                    {
                        // If reading the next member would block, flush the
                        // pipe so that the reader can see all the preceding members
                        // without delay. 
                        // 
                        // Also flush periodically to avoid the sending buffers from
                        // growing too much.  But, do not flush on every iteration;
                        // otherwise a reader over the network might get one packet
                        // for every entry!
                        if (!memberTask.IsCompleted || 
                            (totalBytes - flushWatermark) > short.MaxValue)
                        {
                            flushWatermark = totalBytes;
                            await writer.FlushAsync(cancellationToken)
                                        .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        // Await in a finally block to avoid compiler warning
                        // that ValueTask would get abandoned when an exception
                        // is thrown, even though in this particulation implementation
                        // it would be harmless.
                        (promiseId, isValid) = await memberTask.ConfigureAwait(false);
                    }

                    var buffer = writer.GetMemory(PromiseId.MaxChars + 2);

                    if (!isValid)
                    {
                        // Exceptions from the promise list are reported as part of the
                        // normal payload, and are not considered to fail the pipe.
                        if (_promiseIds.Exception is Exception exception)
                        {
                            bool isCancellation = exception is OperationCanceledException;
                            string text = isCancellation ? "<CANCELLED>\r\n" : "<FAILED>\r\n";
                            numBytes = Encoding.ASCII.GetBytes(text, buffer.Span);
                            writer.Advance(numBytes);
                            totalBytes += numBytes;
                        }

                        break;
                    }
                    
                    ++index;

                    numBytes = promiseId.FormatAscii(buffer);

                    // Terminate each entry by Internet-standard new-line
                    static int AppendNewLine(Span<byte> line)
                    {
                        line[0] = (byte)'\r';
                        line[1] = (byte)'\n';
                        return 2;
                    }
                    numBytes += AppendNewLine(buffer.Span[numBytes..]);

                    writer.Advance(numBytes);
                    totalBytes += numBytes;
                }
            }
            catch (Exception e) when (toComplete)
            {
                completionException = e;
            }
            finally
            {
                if (toComplete)
                {
                    await writer.CompleteAsync(completionException)
                                .ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public override ContentFormatInfo GetFormatInfo(int format)
            => new("text/plain", isContainer: true, ContentPreference.Fair);

        /// <inheritdoc />
        public override long? GetItemsCount(int format)
            => _promiseIds.IsComplete ? _promiseIds.Count : null;
    }
}
