using Microsoft.Extensions.Primitives;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

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

    bool IPromiseListBuilder.TryComplete(int count, 
                                         Exception? exception,
                                         CancellationToken cancellationToken)
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
            _completionCancellation = cancellationToken;

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
                            (oldPromise.HasCompleteOutput &&
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
            if (t > 0 &&
                _completionException is null &&
                _completionCancellation.IsCancellationRequested)
            {
                _completionException = new OperationCanceledException(_completionCancellation);
            }

            // Do not hold onto the cancellation token
            _completionCancellation = default;

            // Publish all transient promises at the end
            if (t > 0)
            {
                foreach (var (index, promise) in _outstandingPromises!)
                    _promiseIds.TrySetMember(c++, new(index, promise.Id));
            }

            // Convert exception to PromiseExceptionalData
            // FIXME: will this throw an exception itself?
            object? status = _completionException is not null
                            ? new PromiseExceptionalData(_completionException)
                            : null;
                
            _promiseIds.TryComplete(total, status);

            _outstandingPromises = null;
            _completionException = null;
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
    private Dictionary<int, Promise>? _outstandingPromises;

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

    /// <summary>
    /// Saved cancellation from <see cref="IPromiseListBuilder.TryComplete" />
    /// to check when this promise list is really complete.
    /// </summary>
    private CancellationToken _completionCancellation;

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
        : this(promiseStorage, true)
    {
        _outstandingPromises = new(capacity: 64);
    }

    /// <summary>
    /// Construct this object partially, for de-serialization.
    /// </summary>
    private PromiseList(PromiseStorage promiseStorage, bool _)
    {
        _promiseStorage = promiseStorage;
    }

    /// <inheritdoc />
    public override bool IsComplete => _promiseIds.IsComplete;

    /// <inheritdoc />
    public override bool IsFailure => _promiseIds.Status is not null;

    /// <summary>
    /// Any exceptional status that has been reported on termination
    /// of the list.
    /// </summary>
    /// <remarks>
    /// Exceptional status include cancellation.  The original
    /// <see cref="ExceptionData" /> object from .NET is translated
    /// into <see cref="PromiseExceptionalData" /> so it can
    /// be reported back to remote clients.
    /// </remarks>
    public PromiseExceptionalData? ExceptionData 
        => _promiseIds.Status as PromiseExceptionalData;

    /// <summary>
    /// Whether generation of the promise list was stopped
    /// by cancellation.
    /// </summary>
    private bool IsCancellation => ExceptionData?.IsCancellation ?? false;

    /// <inheritdoc />
    public override bool IsTransient => _transientCount > 0 || IsCancellation;

    /// <inheritdoc />
    public override ValueTask<Stream> GetByteStreamAsync(int format, CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        _ = GenerateIntoPipeAsync(pipe.Writer, 
                                  GetOutputImpl(format),
                                  contentTypes: StringValues.Empty,
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
    public override ValueTask WriteToPipeAsync(PipeWriter writer, 
                                               PromiseWriteRequest request, 
                                               CancellationToken cancellationToken)
    {
        if (request.Start != 0)
            throw new NotSupportedException();

        var t = GenerateIntoPipeAsync(writer,
                                      GetOutputImpl(request.Format),
                                      request.InnerFormat,
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
    /// <param name="contentTypes">
    /// Media types desired by the client for an inner item.
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
                                             StringValues contentTypes,
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
                    // For now, do not pass contentTypes to
                    // WriteEndAsync because HeartyClient assumes
                    // exceptions are always serialized as JSON.
                    await impl.WriteEndAsync(this,
                                             writer,
                                             StringValues.Empty,
                                             cancellationToken)
                              .ConfigureAwait(false);
                    break;
                }
                
                ++index;

                numBytes = await impl.WriteItemAsync(this,
                                                     writer,
                                                     ordinal,
                                                     contentTypes, 
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
            0 => new(ServedMediaTypes.TextPlain, ContentPreference.Fair, isContainer: true),
            1 => new(ServedMediaTypes.MultipartParallel, ContentPreference.Good, isContainer: true),
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

    /// <summary>
    /// Get a completed member promise.
    /// </summary>
    /// <param name="index">
    /// The index of the promise within the completed list of promises.
    /// Note it may not be the same as the index as it was originally
    /// written with <see cref="IPromiseListBuilder" />,
    /// when promises do not complete in their original order.
    /// </param>
    /// <param name="cancellationToken">
    /// Can be used to cancel waiting for the member promise to complete.
    /// </param>
    /// <returns>
    /// The completed member promise, or null if <paramref name="index" />
    /// exceeds the number of items in the completed list.
    /// </returns>
    public async ValueTask<Promise?> 
        TryGetMemberPromiseAsync(int index, CancellationToken cancellationToken = default)
    {
        var ((_, id), isValid) = await _promiseIds.TryGetMemberAsync(index, cancellationToken)
                                                  .ConfigureAwait(false);
        if (!isValid)
            return null;

        return _promiseStorage.GetPromiseById(id);
    }

    /// <inheritdoc />
    public override bool TryPrepareSerialization(out PromiseDataSerializationInfo info)
    {
        if (!IsComplete)
        {
            info = default;
            return false;
        }

        PromiseDataSerializationInfo exceptionInfo = default;
        if (ExceptionData?.TryPrepareSerialization(out exceptionInfo) == false)
        {
            info = default;
            return false;
        }

        int count = _promiseIdTotal;

        // Schema:
        //
        //  _commitCount    : int
        //  _transientCount : int
        //  ids             : PromiseId[_promiseIdTotal]
        //  ordinals        : int[_promiseIdTotal]
        //  exceptionData   : byte[exceptionInfo.PayloadLength]

        const int headerOffset = 2 * sizeof(int);
        int idBufferSize = count * sizeof(ulong);
        int ordinalBufferSize = count * sizeof(int);

        info = new PromiseDataSerializationInfo
        {
            PayloadLength = checked(
                headerOffset + idBufferSize + ordinalBufferSize +
                exceptionInfo.PayloadLength),
            SchemaCode = SchemaCode,
            State = this,
            Serializer = static (in PromiseDataSerializationInfo info, Span<byte> buffer)
                            => ((PromiseList)info.State!).Serialize(buffer)
        };

        return true;
    }

    private void Serialize(Span<byte> buffer)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, _committedCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[sizeof(int)..], _transientCount);

        int count = _promiseIdTotal;

        const int headerOffset = 2 * sizeof(int);
        int idBufferSize = count * sizeof(ulong);
        int ordinalBufferSize = count * sizeof(int);

        var idBuffer = buffer.Slice(headerOffset, idBufferSize);
        var ordinalBuffer = buffer.Slice(headerOffset + idBufferSize, ordinalBufferSize);

        // Serialize list of promises
        for (int index = 0; index < count; ++index)
        {
            var t = _promiseIds.TryGetMemberAsync(index, default);
            if (!t.IsCompleted)
                throw new InvalidOperationException("PromiseList must be completed to be serialized. ");

            var ((ordinal, id), isValid) = t.Result;

            if (!isValid)
                throw new InvalidOperationException("Internal count in PromiseList is inconsistent. ");

            BinaryPrimitives.WriteUInt64LittleEndian(idBuffer[(index * sizeof(ulong))..],
                                                     id.RawInteger);

            BinaryPrimitives.WriteInt32LittleEndian(ordinalBuffer[(index * sizeof(int))..],
                                                    ordinal);
        }

        // Serialize termination exception, if any
        if (ExceptionData?.TryPrepareSerialization(out var exceptionInfo) == true)
        {
            exceptionInfo.Serializer!.Invoke(
                exceptionInfo,
                buffer[(headerOffset + idBufferSize + ordinalBufferSize)..]);
        }
    }

    /// <summary>
    /// Schema code for serialization.
    /// </summary>
    public const ushort SchemaCode = (byte)'L' | ((byte)'i' << 8);

    /// <summary>
    /// De-serialize an instance of this class
    /// from its serialization created from <see cref="Serialize" />.
    /// </summary>
    /// <param name="fixtures">
    /// Needed to look up promises from their IDs stored in the serialization.
    /// </param>
    /// <param name="buffer">
    /// The buffer of bytes to de-serialize from.
    /// </param>
    public static PromiseList Deserialize(IPromiseDataFixtures fixtures, 
                                          ReadOnlySpan<byte> buffer)
    {
        var promiseStorage = fixtures.PromiseStorage;

        // Read counts
        int committedCount = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        int transientCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[sizeof(int)..]);
        if (committedCount < 0 || transientCount < 0)
            throw new InvalidDataException("The counts of promises are the serialized PromiseList is invalid. ");

        int count = checked(committedCount + transientCount);

        const int headerOffset = 2 * sizeof(int);
        int idBufferSize = count * sizeof(ulong);
        int ordinalBufferSize = count * sizeof(int);

        var idBuffer = buffer.Slice(headerOffset, idBufferSize);
        var ordinalBuffer = buffer.Slice(headerOffset + idBufferSize, ordinalBufferSize);

        // Instantiate object with correct counts
        var self = new PromiseList(promiseStorage, false)
        {
            _committedCount = committedCount,
            _transientCount = transientCount,
            _promisesSeen = count,
            _promiseIdTotal = count
        };

        // Populate Promise IDs and ordinals
        for (int index = 0; index < count; ++index)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(idBuffer[(index * sizeof(ulong))..]);
            int ordinal = BinaryPrimitives.ReadInt32LittleEndian(ordinalBuffer[(index * sizeof(ulong))..]);
            self._promiseIds.TrySetMember(index, KeyValuePair.Create(ordinal, new PromiseId(id)));
        }

        // De-serialize termination exception if any
        PromiseExceptionalData? exceptionData = null;
        var exceptionBuffer = buffer[(headerOffset + idBufferSize + ordinalBufferSize)..];
        if (exceptionBuffer.Length > 0)
            exceptionData = PromiseExceptionalData.Deserialize(fixtures, exceptionBuffer);

        self._promiseIds.TryComplete(count, exceptionData);
        return self;
    }
}
