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
    /// Provides (remote) clients with various ways to consume outputs from a (shared) promise.
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
    /// </remarks>
    public abstract class PromiseOutput
    {
        /// <summary>
        /// Called to clean up any resources when the <see cref="Promise"/> owning this result expires.
        /// </summary>
        protected virtual void Dispose() { }

        /// <summary>
        /// Prepare to incrementally read the byte stream for the payload
        /// with efficient buffer management.
        /// </summary>
        public abstract ValueTask<PipeReader> 
            GetPipeReaderAsync(string contentType, 
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
    }
}
