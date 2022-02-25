using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server
{
    /// <summary>
    /// The output of a "macro job" which expands to an
    /// asynchronously-produced sequence of other promises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The promises which are set through the <see cref="IPromiseListBuilder"/>
    /// interface are always re-ordered so that promises that complete first
    /// go in front.  This re-ordering produces output that is not deterministic
    /// although of course it stays the same when a particular instance of
    /// this class is queried over and over again.  The re-ordering
    /// makes the code more complex, but is, unfortunately, not optional:
    /// <list type="bullet">
    /// <item>
    /// When the results are streamed, and the different promises in the list
    /// take different times to compute, if the results are not re-ordered 
    /// then there may be "head-of-line" blocking of results that are
    /// already available by a promise that takes really long to calculate.
    /// </item>
    /// <item>
    /// When a macro job that generates promises to store into this list
    /// gets cancelled, the individual promises get cancelled too, of course.
    /// But if multiple clients simultaneously run macro jobs that write 
    /// to a common <see cref="PromiseList" />, but one client is further
    /// along, yet gets cancelled, then the cancelled results would get
    /// committed to <see cref="PromiseList" />.  But clients that have
    /// not cancelled should not see such promises!
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// For cancelled promises, or more generally promises with transient
    /// results, they are only committed if <see cref="IPromiseListBuilder" />
    /// gets completed.  A macro job that is shared but is cancellation, then,
    /// must never complete <see cref="IPromiseListBuilder" /> even with
    /// <see cref="OperationCanceledException" />.
    /// </para>
    /// </remarks>
    public partial class PromiseList : PromiseData, IPromiseListBuilder
    {
        #region Implementation of IPromiseListBuilder

        /// <summary>
        /// The list of promise IDs after re-ordering.
        /// </summary>
        /// <remarks>
        /// The integer key is the index
        /// of the promise in the original input ordering.
        /// </remarks>
        private readonly IncrementalAsyncList<KeyValuePair<int, PromiseId>> 
            _promiseIds = new();

        bool IPromiseListBuilder.IsComplete => _promiseIds.IsComplete;

        ValueTask IPromiseListBuilder.WaitForAllPromisesAsync() 
            => new ValueTask(_promiseIds.Completion);

        bool IPromiseListBuilder.TryComplete(int count, Exception? exception)
        {
            var outstandingPromises = _outstandingPromises;
            if (outstandingPromises is null)
                return false;

            lock (outstandingPromises)
            {
                if (_outstandingPromises is null)
                    return false;

                if (_promisesSeen != count)
                    throw new InvalidOperationException("The count of promises reported on completion does not match the actual count. ");

                if (_promiseIdTotal >= 0)
                    return false;

                _promiseIdTotal = count;
                _completionException = exception;

                FinishUpWhenAllPromisesCompleted();
            }

            return true;
        }

        void IPromiseListBuilder.SetMember(int index, Promise promise)
        {
            var outstandingPromises = _outstandingPromises;
            if (outstandingPromises is null)
                return;

            lock (outstandingPromises)
            {
                if (_outstandingPromises is null)
                    return;

                int count = _promisesSeen;
                if (index < 0 || index > count)
                {
                    throw new IndexOutOfRangeException(
                        "A new index to register a new promise must " +
                        "immediately follow the last highest index. ");
                }

                if (index == count)
                {
                    outstandingPromises.Add(index, promise);
                    ++_promisesSeen;
                }
                else
                {
                    // If index < count, the promise must have been seen
                    // and completed with a non-transient result.
                    if (!outstandingPromises.TryGetValue(index, out var oldPromise))
                        return;

                    bool keep = object.ReferenceEquals(oldPromise, promise) ||
                                (oldPromise.IsCompleted &&
                                 !oldPromise.ResultOutput!.IsTransient);
                    if (keep)
                        return;

                    outstandingPromises[index] = promise;
                }
            }

            _ = WaitForPromiseAsync(index, promise);
        }

        /// <summary>
        /// Waits for a promise to complete, and if it has a non-transient 
        /// result, commits it into the final list of promise IDs.
        /// </summary>
        /// <param name="index">
        /// The index of the promise in the original input ordering.
        /// </param>
        /// <param name="promise">
        /// The promise to wait for.
        /// </param>
        private async Task WaitForPromiseAsync(int index, Promise promise)
        {
            var result = await promise.GetResultAsync(dummyClient, null, default)
                                      .ConfigureAwait(false);

            var outstandingPromises = _outstandingPromises;
            if (outstandingPromises is null)
                return;

            lock (outstandingPromises)
            {
                if (_outstandingPromises is null)
                    return;

                if (!outstandingPromises.TryGetValue(index, out var oldPromise) ||
                    !object.ReferenceEquals(oldPromise, promise))
                    return;

                if (result.NormalOutput.IsTransient)
                {
                    ++_transientCount;
                }
                else
                {
                    _promiseIds.TrySetMember(_committedCount++,
                                             new(index, promise.Id));
                    outstandingPromises.Remove(index);
                }
                    
                FinishUpWhenAllPromisesCompleted();
            }
        }

        /// <summary>
        /// Complete the list of promise IDs in the final ordering
        /// when there are no more promises to await.
        /// </summary>
        private void FinishUpWhenAllPromisesCompleted()
        {
            int total = _promiseIdTotal;
            int c = _committedCount;
            int t = _transientCount;

            if (c + t == total)
            {
                // Publish all transient promises at the end
                if (t > 0)
                {
                    foreach (var (index, promise) in _outstandingPromises!)
                        _promiseIds.TrySetMember(c++, new(index, promise.Id));
                }

                _promiseIds.TryComplete(total, _completionException);

                _outstandingPromises = null;
            }
        }
            
        PromiseData IPromiseListBuilder.Output => this;

        /// <summary>
        /// Buffer of promises that need to be awaited before
        /// they can be committed into <see cref="_promiseIds" />.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The key is the index of the promise in the original input
        /// ordering.  A dictionary is used in place of an array since
        /// it is expected only that the most recent promises,
        /// out of potentially thousands, need to be remembered; 
        /// once a promise completes with a non-transient result, 
        /// its entry can be removed from this dictionary entirely.
        /// </para>
        /// <para>
        /// This dictionary is set to null once this <see cref="PromiseList" />
        /// has completed, to free up memory.
        /// </para>
        /// </remarks>
        private Dictionary<int, Promise>? _outstandingPromises = new(capacity: 64);

        /// <summary>
        /// The current count of items seen from calls to
        /// <see cref="IPromiseListBuilder.SetMember" />.
        /// </summary>
        private int _promisesSeen = 0;

        /// <summary>
        /// The total number of promise IDs, determined once 
        /// <see cref="IPromiseListBuilder.TryComplete" /> is called.
        /// </summary>
        /// <remarks>
        /// Before that happens this is set to -1 so that it never
        /// compares equal to the sum of <see cref="_committedCount" />
        /// and <see cref="_transientCount" />.
        /// </remarks>
        private int _promiseIdTotal = -1;

        /// <summary>
        /// Count of promises with non-transient results that have been committed 
        /// into <see cref="_promiseIds" />.
        /// </summary>
        private int _committedCount = 0;

        /// <summary>
        /// Count of promises with transient results that are being held back.
        /// </summary>
        private int _transientCount = 0;

        /// <summary>
        /// Saved exception from <see cref="IPromiseListBuilder.TryComplete" />
        /// (before it is set into <see cref="_promiseIds" />).
        /// </summary>
        private Exception? _completionException;

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
        public override bool IsComplete => _promiseIds.IsComplete;

        /// <inheritdoc />
        public override bool IsFailure => _promiseIds.Exception is not null;

        /// <inheritdoc />
        public override bool IsTransient
            => _promiseIds.Exception is OperationCanceledException ||
               _transientCount > 0;

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
                    int ordinal;
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
                        ((ordinal, promiseId), isValid) = await memberTask.ConfigureAwait(false);
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
                                                         ordinal,
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
