using Microsoft.Extensions.Primitives;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server
{
    /// <summary>
    /// Provides (remote) clients with various ways to consume inputs/outputs from a (shared) promise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Various forms of streaming is used to optimize for large payloads or progressively-produced
    /// payloads.  However, there can be more than one client downloading at different positions, 
    /// i.e. the results are not broadcast (over some messaging bus).  So this class is provided
    /// to obtain the necessary objects to support the asynchronous operations for each client.
    /// </para>
    /// <para>
    /// The asynchronous task objects returned by some methods here are, theoretically speaking,
    /// not necessary, since the objects being wrapped support asynchronous operations themselves.
    /// But the implementation is eased greatly by allowing the flexibility, without any serious
    /// performance hit.  Often, you may want to use off-the-shelf implementations of classes
    /// like <see cref="PipeReader" />, whose constructors would require some parameters,
    /// like what source they are reading from, to be referenced up-front.  Suppose those parameters
    /// require some sort of asynchronous initialization, such as opening a connection to a
    /// caching database.  Without the "outer" task objects, you would be forced to wrap the 
    /// off-the-shelf implementations to await the source and then forward all methods.   
    /// </para>
    /// <para>
    /// This class is used to represent not only a promise's result, but also the request data
    /// that is output by the server when clients query it.
    /// </para>
    /// </remarks>
    public abstract class PromiseData
    {
        /// <summary>
        /// Called to clean up any resources when the <see cref="Promise"/> owning this result expires.
        /// </summary>
        protected virtual void Dispose() { }

        /// <summary>
        /// Upload the stream of bytes for the payload into a pipe, asynchronously.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method is designed for transmitting 
        /// the payload to a remote host, by more precise 
        /// and efficient control of buffering than is possible with 
        /// <see cref="GetByteStreamAsync" />.  The caller, with
        /// knowledge of the transmission mechanism, can influence
        /// buffering through the implementation of <see cref="PipeWriter" />
        /// that it passes in.  
        /// </para>
        /// <para>
        /// The callee does not have the freedom to choose its 
        /// implementation of <see cref="PipeWriter" />.  But that
        /// is probably irrelevant:  the callee must have some
        /// kind of backing storage for the payload, as it must be
        /// persisted between multiple calls to any methods of this class,
        /// and so a pipe (which is only good to consume once)
        /// could not form the native representation of the payload anyway.
        /// </para>
        /// <para>
        /// Nevertheless, if the caller desires to consume and parse 
        /// the payload in-process, it can simply create a <see cref="Pipe" />
        /// to obtain a <see cref="PipeReader" /> to read the payload from.
        /// In fact, <see cref="GetByteStreamAsync" /> might be implemented
        /// exactly this way.
        /// </para>
        /// </remarks>
        /// <param name="writer">
        /// The pipe to write the data into.
        /// </param>
        /// <param name="request">
        /// Specifies what to write into the pipe.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to interrupt the writing into the pipe.
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
        public abstract ValueTask
            WriteToPipeAsync(PipeWriter writer,
                             PromiseWriteRequest request,
                             CancellationToken cancellationToken);

        /// <summary>
        /// Upload the stream of bytes for the payload into a pipe, asynchronously.
        /// </summary>
        /// <remarks>
        /// This method is a less general version of 
        /// <see cref="WriteToPipeAsync(PipeWriter, PromiseWriteRequest, CancellationToken)" />
        /// which does not allow control over the range of data written or the
        /// format of the individual items.
        /// </remarks>
        /// <param name="writer">
        /// The pipe to write the data into.
        /// </param>
        /// <param name="format">
        /// The desired format of the data to present in
        /// the stream.  Must be in the range of 0 to 
        /// <see cref="CountFormats" /> minus one.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to interrupt the writing into the pipe.
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
        public ValueTask
            WriteToPipeAsync(PipeWriter writer,
                             int format,
                             CancellationToken cancellationToken)
            => WriteToPipeAsync(writer,
                                new PromiseWriteRequest { Format = format },
                                cancellationToken);

        /// <summary>
        /// Prepare to read the byte stream for the payload.
        /// </summary>
        /// <param name="format">
        /// The desired format of the data to present in
        /// the stream.  Must be in the range of 0 to 
        /// <see cref="CountFormats" /> minus one.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel retrieval of the stream, if
        /// it must be asynchronous.
        /// </param>
        /// <returns>
        /// Asynchronous task that provides the readable I/O stream.
        /// The stream should positioned at the beginning;
        /// if the format supports seeking by bytes
        /// (indicated by <see cref="ContentSeekability.Bytes" />
        /// <see cref="ContentSeekability.Both" />), then the stream
        /// should be seekable.  The stream should also report
        /// its length if <see cref="GetContentLength(int)" />
        /// returns non-null for the same format.
        /// </returns>
        public abstract ValueTask<Stream>
            GetByteStreamAsync(int format,
                               CancellationToken cancellationToken);

        /// <summary>
        /// Get the payload as blocks of bytes in memory.
        /// </summary>
        /// <param name="format">
        /// The desired format of the data to present.  Must 
        /// be in the range of 0 to <see cref="CountFormats" /> minus one.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel retrieving and/or generating
        /// the blocks of bytes.
        /// </param>
        /// <returns>
        /// The payload as a sequence of blocks of bytes,
        /// which may in fact be what is internally stored
        /// by the implementation.
        /// </returns>
        public abstract ValueTask<ReadOnlySequence<byte>> 
            GetPayloadAsync(int format, 
                            CancellationToken cancellationToken);

        /// <summary>
        /// The IANA media type that is the default suggestion for the payload.
        /// </summary>
        /// <remarks>
        /// This class allows an implementation to support negotiation for different
        /// content types, as in HTTP.  Obviously there must be at least one type
        /// for the payload that is supported.  Naturally it would be the "native"
        /// or "default" serialization of the underlying data, which this property
        /// can refer to.
        /// </remarks>
        /// </summary>
        public string ContentType => GetFormatInfo(0).MediaType;

        /// <summary>
        /// The total number of bytes in the data expressed in 
        /// the default format, if known.
        /// </summary>
        public long? ContentLength => GetContentLength(0);

        /// <summary>
        /// Get the number of (alternative) formats for the data.
        /// </summary>
        /// <remarks>
        /// This value must be at least one.
        /// </remarks>
        public virtual int CountFormats => 1;

        /// <summary>
        /// Get a description of an available data format.
        /// </summary>
        /// <param name="format">
        /// Integer index selecting the format, ranging from 0 to
        /// <see cref="CountFormats" /> minus one.
        /// </param>
        /// <returns>
        /// Description of the selected format, used for content negotiation.
        /// </returns>
        /// <remarks>
        /// The format at index 0 must be the format to be 
        /// offered by default to clients if they do not filter.
        /// </remarks>
        public abstract ContentFormatInfo GetFormatInfo(int format);

        /// <summary>
        /// Format specifications from <see cref="PromiseData" /> encapsulated
        /// into a read-only list.
        /// </summary>
        private readonly struct ContentFormatInfoList : IReadOnlyList<ContentFormatInfo>
        {
            private readonly PromiseData _parent;

            public ContentFormatInfoList(PromiseData parent) => _parent = parent;

            /// <summary>
            /// Returns the value from <see cref="PromiseData.GetFormatInfo(int)" />.
            /// </summary>
            public ContentFormatInfo this[int index] => _parent.GetFormatInfo(index);

            /// <summary>
            /// Returns the value of <see cref="PromiseData.CountFormats" />.
            /// </summary>
            public int Count => _parent.CountFormats;

            /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />
            public IEnumerator<ContentFormatInfo> GetEnumerator()
            {
                for (int i = 0; i < Count; ++i)
                    yield return this[i];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// Choose a format of output from this instance
        /// that best matches a set of requested patterns.
        /// </summary>
        /// <param name="requestedTypes">
        /// The list of IANA media types, or patterns of media types,
        /// accepted by the client.  The strings follow the format 
        /// of the "Accept" header in the HTTP/1.1 specification.
        /// If empty, this method simply returns 0, referring
        /// to the first element of <paramref name="responses" />.
        /// </param>
        /// <returns>
        /// The index of the format, ranging from 0 to 
        /// <see cref="CountFormats" /> minus one, 
        /// that best matches the client's requests, 
        /// or -1 if none of the available formats is acceptable
        /// from <paramref name="requestedTypes" />.
        /// </returns>
        public int NegotiateFormat(StringValues requestedTypes)
            => ContentFormatInfo.Negotiate(new ContentFormatInfoList(this), 
                                           requestedTypes);

        /// <summary>
        /// Get the length of the data, in bytes, for a given format.
        /// </summary>
        /// <param name="format">
        /// Integer index selecting the format, ranging from 0 to
        /// <see cref="CountFormats" /> minus one.
        /// </param>
        /// <returns>
        /// The length in bytes, if it is available. 
        /// </returns>
        public virtual long? GetContentLength(int format) => null;

        /// <summary>
        /// Get the number of items, for formats containing 
        /// multi-valued data.
        /// </summary>
        /// <param name="format">
        /// Integer index selecting the format, ranging from 0 to
        /// <see cref="CountFormats" /> minus one.
        /// </param>
        /// Null if the data is not multi-valued or the number
        /// of items is not known at this point.
        /// </returns>
        public virtual long? GetItemsCount(int format) => null;

        /// <summary>
        /// Whether this data represents a failure condition.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Some consumers may take a true value as a hint to wrap
        /// the data in an exception or other wrapper they conventionally
        /// use to report failures.  Generally, payloads that represent
        /// errors raised by the promises or job execution system would get 
        /// will have this property report true.  This property also
        /// reports true for the output from a job being cancelled.
        /// </para>
        /// <para>
        /// But it is unspecified whether problems from application-level 
        /// processing are flagged as failures here.  Some applications
        /// may prefer to report their anomalous conditions as normal
        /// payloads, possibly with a distinguished content type.
        /// </para>
        /// </remarks>
        public virtual bool IsFailure => false;

        /// <summary>
        /// Whether this data is transient result that should be 
        /// automatically re-tried if the containing 
        /// promise is requested to be "run" (not merely queried).
        /// </summary>
        /// <remarks>
        /// This property is a key characteristic of cancellation.
        /// If a job associated to this promise has been cancelled
        /// (by other clients), requesting it as a job should restart
        /// the job.  Other kinds of failures may mark themselves
        /// as transient as well.
        /// </remarks>
        public virtual bool IsTransient => false;

        /// <summary>
        /// True if this instance holds data in its final form;
        /// false if its data is partial.
        /// </summary>
        public virtual bool IsComplete => true;
    }

    /// <summary>
    /// Input parameters for <see cref="PromiseData.WriteToPipeAsync" />
    /// </summary>
    public readonly struct PromiseWriteRequest
    {
        /// <summary>
        /// The desired format of the data to write.  
        /// </summary>
        /// <remarks>
        /// This value must be in the range of 0 to 
        /// <see cref="PromiseData.CountFormats" /> minus one.
        /// For promises that are containers of other promises,
        /// this format refers to that of the container.
        /// To map from a content type specified as a string
        /// to a value for this member, call
        /// <see cref="PromiseData.NegotiateFormat(StringValues)" />.
        /// </remarks>
        public int Format { get; init; }

        /// <summary>
        /// The desired starting position in the data.
        /// </summary>
        public long Start { get; init; }

        /// <summary>
        /// The desired ending position in the data.
        /// </summary>
        public long End { get; init; } = -1;

        /// <summary>
        /// The desired format of the data of the individual
        /// items of the container.
        /// </summary>
        /// <remarks>
        /// Unfortunately the format cannot be negotiated
        /// up front and resolved to an integer
        /// (like <see cref="Format" />) because the individual
        /// promises may be incrementally generated and are 
        /// not immediately available.  Thus, the implementation
        /// of the (streaming) container must perform content
        /// negotiation itself, and if it fails, report
        /// that error within the payload of the container.
        /// </remarks>
        public string? InnerFormat { get; init; }
    }
}
