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
        public override ValueTask<PipeReader> GetPipeReaderAsync(string contentType, long position, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetPipeReaderInternal(contentType, position, cancellationToken));

        private PipeReader GetPipeReaderInternal(string contentType, long position, CancellationToken cancellationToken)
        {
            VerifyContentType(contentType);
            var pipeReader = PipeReader.Create(Body);
            if (position > 0)
                pipeReader.AdvanceTo(Body.GetPosition(position));
            return pipeReader;
        }

        /// <inheritdoc />
        public override ValueTask<Stream> GetByteStreamAsync(string contentType, CancellationToken cancellationToken)
        {
            VerifyContentType(contentType);
            var pipeReader = GetPipeReaderInternal(contentType, 0, cancellationToken);
            return ValueTask.FromResult(pipeReader.AsStream());
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
        public override ValueTask<IAsyncEnumerator<ReadOnlyMemory<byte>>> GetPayloadStreamAsync(string contentType, int position, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
