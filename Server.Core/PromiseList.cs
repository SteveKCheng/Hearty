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
        public override string SuggestedContentType => "text/plain";

        /// <inheritdoc />
        public override ValueTask<Stream> GetByteStreamAsync(string contentType, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetPipeReader(contentType, cancellationToken).AsStream());

        /// <inheritdoc />
        public override ValueTask<ReadOnlySequence<byte>> GetPayloadAsync(string contentType, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override ValueTask<IAsyncEnumerator<ReadOnlyMemory<byte>>> GetPayloadStreamAsync(string contentType, int position, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override async ValueTask<PipeReader> GetPipeReaderAsync(string contentType, long position, CancellationToken cancellationToken)
        {
            var pipeReader = GetPipeReader(contentType, cancellationToken);

            // Skip bytes at beginning
            while (position > 0)
            {
                var readResult = await pipeReader.ReadAsync(cancellationToken)
                                                 .ConfigureAwait(false);
                if (readResult.IsCompleted)
                    break;
                var skip = Math.Min(position, readResult.Buffer.Length);
                pipeReader.AdvanceTo(readResult.Buffer.GetPosition(skip));
                position -= skip;
            }

            return pipeReader;
        }

        private PipeReader GetPipeReader(string contentType, CancellationToken cancellationToken)
        {
            var pipe = new Pipe();
            _ = GenerateListIntoPipeAsync(pipe.Writer, cancellationToken);
            return pipe.Reader;
        }

        /// <summary>
        /// Asynchronously write the list of promise IDs in plain-text form into a pipe.
        /// </summary>
        private async Task GenerateListIntoPipeAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new byte[PromiseId.MaxChars + 2];
                var memory = new Memory<byte>(buffer);

                int index = 0;

                while (true)
                {
                    int numBytes;

                    var (promiseId, isValid) = await _promiseIds.TryGetMemberAsync(
                                                        index, cancellationToken);
                    if (!isValid)
                    {
                        // Exceptions from the promise list are reported as part of the
                        // normal payload, and are not considered to fail the pipe.
                        if (_promiseIds.Exception is Exception exception)
                        {
                            bool isCancellation = exception is OperationCanceledException;
                            string text = isCancellation ? "<CANCELLED>\r\n" : "<FAILED>\r\n";
                            numBytes = Encoding.ASCII.GetBytes(text, memory.Span);
                            await writer.WriteAsync(memory[..numBytes], cancellationToken)
                                        .ConfigureAwait(false);
                        }

                        break;
                    }
                    
                    ++index;

                    numBytes = promiseId.FormatAscii(memory);

                    // Terminate each entry by Internet-standard new-line
                    buffer[numBytes++] = (byte)'\r';
                    buffer[numBytes++] = (byte)'\n';

                    await writer.WriteAsync(memory[..numBytes], cancellationToken)
                                .ConfigureAwait(false);
                }

                await writer.CompleteAsync()
                            .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                await writer.CompleteAsync(e)
                            .ConfigureAwait(false);
            }
        }
    }
}
