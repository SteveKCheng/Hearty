using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// The output of a "macro job" which expands to an
    /// asynchronously-produced sequence of other promises.
    /// </summary>
    public partial class PromiseList : PromiseData, IPromiseListBuilder
    {
        #region Implementation of IPromiseListBuilder

        private readonly IncrementalAsyncList<PromiseId> _promiseIds = new();

        bool IPromiseListBuilder.IsComplete => _promiseIds.IsComplete;

        bool IPromiseListBuilder.IsCancelled =>
            _promiseIds.IsComplete && _promiseIds.Exception is OperationCanceledException;

        bool IPromiseListBuilder.TryComplete(int count, Exception? exception)
            => _promiseIds.TryComplete(count, exception);

        void IPromiseListBuilder.SetMember(int index, Promise promise)
            => _promiseIds.TrySetMember(index, promise.Id);
        PromiseData IPromiseListBuilder.Output => this;

        #endregion

        private readonly PromiseStorage _promiseStorage;

        /// <summary>
        /// Construct with an initially empty and uncompleted list.
        /// </summary>
        /// <param name="promiseStorage">
        /// Needed to look up promises given their IDs, as
        /// they are set through <see cref="IPromiseListBuilder" />.
        /// </param>
        public PromiseList(PromiseStorage promiseStorage)
        {
            _promiseStorage = promiseStorage;
        }

        /// <inheritdoc />
        public override ValueTask<Stream> GetByteStreamAsync(int format, CancellationToken cancellationToken)
        {
            var pipe = new Pipe();
            _ = GenerateIntoPipeAsync(pipe.Writer, 
                                      GetOutputImpl(format),
                                      toComplete: true, 
                                      cancellationToken);
            return ValueTask.FromResult(pipe.Reader.AsStream());
        }

        /// <inheritdoc />
        public override ValueTask<ReadOnlySequence<byte>> GetPayloadAsync(int format, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override ValueTask WriteToPipeAsync(int format, PipeWriter writer, long position, CancellationToken cancellationToken)
        {
            if (position != 0)
                throw new NotSupportedException();

            var t = GenerateIntoPipeAsync(writer,
                                          GetOutputImpl(format),
                                          toComplete: false, 
                                          cancellationToken);
            return new ValueTask(t);
        }

        /// <summary>
        /// Asynchronously generate the list of items into a pipe.
        /// </summary>
        /// <param name="writer">Where to write to. </param>
        /// <param name="impl">The virtual methods to write
        /// items in a particular format.
        /// </param>
        /// <param name="toComplete">
        /// If true, this method completes the pipe, possibly 
        /// with an exception, when all items are written.  
        /// If false, the pipe is left uncompleted.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to interrupt writing.
        /// </param>
        private async Task GenerateIntoPipeAsync(PipeWriter writer, 
                                                 OutputImpl impl,
                                                 bool toComplete, 
                                                 CancellationToken cancellationToken)
        {
            Exception? completionException = null;

            try
            {
                int index = 0;
                int unflushedBytes = 0;

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
                        if (!memberTask.IsCompleted || unflushedBytes > short.MaxValue)
                        {
                            await writer.FlushAsync(cancellationToken)
                                        .ConfigureAwait(false);
                            unflushedBytes = 0;
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

                    if (!isValid)
                    {
                        await impl.WriteEndAsync(this,
                                                 writer,
                                                 _promiseIds.Exception,
                                                 cancellationToken)
                                  .ConfigureAwait(false);
                        break;
                    }
                    
                    ++index;

                    numBytes = await impl.WriteItemAsync(this,
                                                         writer,
                                                         promiseId,
                                                         cancellationToken)
                                         .ConfigureAwait(false);

                    unflushedBytes = (numBytes >= 0)
                                        ? unflushedBytes + numBytes
                                        : 0;
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

        #region Output formats

        /// <inheritdoc />
        public override int CountFormats => 2;

        /// <inheritdoc />
        public override ContentFormatInfo GetFormatInfo(int format)
        {
            return format switch
            {
                0 => new("text/plain", isContainer: true, ContentPreference.Fair),
                1 => new("multipart/parallel; boundary=#", isContainer: true, ContentPreference.Good),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
        }

        /// <inheritdoc />
        public override long? GetItemsCount(int format)
            => _promiseIds.IsComplete ? _promiseIds.Count : null;


        private static OutputImpl GetOutputImpl(int format)
            => format switch
            {
                0 => PlainTextImpl.Instance,
                1 => MultiPartImpl.Instance,
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };

        #endregion
    }
}
