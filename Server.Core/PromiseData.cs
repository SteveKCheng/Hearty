using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
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
        /// <param name="contentType">
        /// Desired content type of the payload.
        /// </param>
        /// <param name="writer">
        /// The pipe to write the data into.
        /// </param>
        /// <param name="position">
        /// The position, measured in number of bytes 
        /// from the beginning, to start writing the payload from.
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
            WriteToPipeAsync(string contentType,
                             PipeWriter writer,
                             long position, 
                             CancellationToken cancellationToken);

        /// <summary>
        /// Prepare to read the byte stream for the payload.
        /// </summary>
        public abstract ValueTask<Stream>
            GetByteStreamAsync(string contentType,
                           CancellationToken cancellationToken);

        /// <summary>
        /// Get the payload as a contiguous block of bytes.
        /// </summary>
        public abstract ValueTask<ReadOnlySequence<byte>> 
            GetPayloadAsync(string contentType, 
                            CancellationToken cancellationToken);

        /// <summary>
        /// Get the sequence of results where each item can be treated as a "message" 
        /// consisting of one block of bytes.
        /// </summary>
        public abstract ValueTask<IAsyncEnumerator<ReadOnlyMemory<byte>>> 
            GetPayloadStreamAsync(string contentType,
                                  int position,
                                  CancellationToken cancellationToken);

        /// <summary>
        /// The IANA media type that is the default choice for the payload.
        /// </summary>
        /// <remarks>
        /// This class allows an implementation to support negotiation for different
        /// content types, as in HTTP.  Obviously there must be at least one type
        /// for the payload that is supported.  Naturally it would be the "native"
        /// or "default" serialization of the underlying data, which this property
        /// can refer to.
        /// </remarks>
        /// </summary>
        public abstract string SuggestedContentType { get; }

        /// <summary>
        /// The number of items that would come from the object returned
        /// by <see cref="GetPayloadStreamAsync" />, starting from the beginning, 
        /// if known.
        /// </summary>
        public virtual int? ItemsCount { get => null; }

        /// <summary>
        /// The total number of bytes that would come from the object returned
        /// by <see cref="GetPipeStreamAsync" />, starting from the beginning, 
        /// if known.
        /// </summary>
        public virtual long? ContentLength { get => null; }

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
        public virtual bool IsFailure { get => false; }
    }
}
