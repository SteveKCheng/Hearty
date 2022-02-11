using JobBank.Utilities;
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
    /// A payload, stored in memory that can be uploaded for a new job,
    /// or downloaded from a promise.
    /// </summary>
    public sealed class Payload : PromiseData
    {
        /// <summary>
        /// Content (media) type describing the data format of <see cref="Body"/>,
        /// specified in the same way as HTTP and MIME.
        /// </summary>
        public override string SuggestedContentType { get; }

        /// <inheritdoc />
        public override long? ContentLength => Body.Length;

        /// <inheritdoc />
        public override bool IsFailure { get; }

        /// <summary>
        /// The sequence of bytes forming the user-defined data.
        /// </summary>
        public ReadOnlySequence<byte> Body { get; }

        /// <summary>
        /// Provide a sequence of bytes in memory, 
        /// labelled with a fixed content type.
        /// </summary>
        /// <param name="contentType"><see cref="SuggestedContentType"/>. </param>
        /// <param name="body">Sequence of bytes, possibly in multiple buffers chained together. </param>
        /// <param name="isFailure">Whether this payload encapsulates a failure condition,
        /// as defined by <see cref="PromiseData.IsFailure" />.
        /// </param>
        public Payload(string contentType, in ReadOnlySequence<byte> body, bool isFailure = false)
        {
            SuggestedContentType = contentType;
            Body = body;
            IsFailure = isFailure;
        }

        /// <summary>
        /// Provide a sequence of bytes in one contiguous memory buffer, 
        /// labelled with a fixed content type.
        /// </summary>
        /// <param name="contentType"><see cref="SuggestedContentType"/>. </param>
        /// <param name="body">Sequence of bytes in one contiguous member buffer. </param>
        /// <param name="isFailure">Whether this payload encapsulates a failure condition,
        /// as defined by <see cref="PromiseData.IsFailure" />.
        /// </param>
        public Payload(string contentType, in ReadOnlyMemory<byte> body, bool isFailure = false)
            : this(contentType, new ReadOnlySequence<byte>(body), isFailure)
        {
        }

        /// <inheritdoc />
        public override ValueTask WriteToPipeAsync(string contentType, PipeWriter writer, long position, CancellationToken cancellationToken)
        {
            VerifyContentType(contentType);
            return writer.WriteAsync(Body.Slice(position), cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask<Stream> GetByteStreamAsync(string contentType, CancellationToken cancellationToken)
        {
            VerifyContentType(contentType);
            Stream stream = new MemoryReadingStream(Body);
            return ValueTask.FromResult(stream);
        }

        private void VerifyContentType(string contentType)
        {
            if (contentType != SuggestedContentType)
                throw new NotSupportedException($"Requested content type {contentType} not supported for this promise output. ");
        }

        /// <inheritdoc />
        public override ValueTask<ReadOnlySequence<byte>> GetPayloadAsync(string contentType, CancellationToken cancellationToken)
        {
            VerifyContentType(contentType);
            return ValueTask.FromResult(Body);
        }

        /// <inheritdoc />
        public override ContentFormatInfo GetFormatInfo(int format)
            => new(SuggestedContentType,
                   ContentPreference.Best,
                   ContentSeekability.Bytes);

        /// <inheritdoc />
        public override long? GetContentLength(int format) => Body.Length;
    }
}
