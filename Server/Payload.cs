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
    public sealed class Payload : PromiseOutput
    {
        /// <summary>
        /// Content (media) type describing the data format of <see cref="Body"/>,
        /// specified in the same way as HTTP and MIME.
        /// </summary>
        public override string SuggestedContentType { get; }

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
        public Payload(string contentType, in ReadOnlySequence<byte> body)
        {
            SuggestedContentType = contentType;
            Body = body;
        }

        /// <summary>
        /// Provide a sequence of bytes in one contiguous memory buffer, 
        /// labelled with a fixed content type.
        /// </summary>
        /// <param name="contentType"><see cref="SuggestedContentType"/>. </param>
        /// <param name="body">Sequence of bytes in one contiguous member buffer. </param>
        public Payload(string contentType, in ReadOnlyMemory<byte> body)
            : this(contentType, new ReadOnlySequence<byte>(body))
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
