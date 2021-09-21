using Nerdbank.Streams;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Encapsulates a payload, stored in memory, of arbitrary content (media) type
    /// that is uploaded or downloaded.
    /// </summary>
    public sealed class Payload : PromiseOutput, IEquatable<Payload>
    {
        /// <summary>
        /// Content (media) type describing the data format of <see cref="Body"/>,
        /// specified in the same way as HTTP and MIME.
        /// </summary>
        public override string SuggestedContentType { get; }

        /// <summary>
        /// The sequence of bytes forming the user-defined data.
        /// </summary>
        public Memory<byte> Body { get; }

        /// <summary>
        /// Label a sequence of bytes with its content type.
        /// </summary>
        /// <param name="contentType"><see cref="SuggestedContentType"/>. </param>
        /// <param name="body"><see cref="Body"/>. </param>
        public Payload(string contentType, Memory<byte> body)
        {
            SuggestedContentType = contentType;
            Body = body;
        }

        /// <summary>
        /// A payload equals another if they have the same content type
        /// (compared as strings) and the contents are byte-wise equal.
        /// </summary>
        /// <param name="other">The payload to compare this payload against. </param>
        public bool Equals(Payload? other)
            => other != null && SuggestedContentType == other.SuggestedContentType && Body.Span.SequenceEqual(other.Body.Span);

        /// <inheritdoc />
        public override bool Equals(object? obj)
            => obj is Payload payload && Equals(payload);

        /// <inheritdoc />
        public override int GetHashCode()
            => SuggestedContentType.GetHashCode();   // FIXME should hash first few bytes of Body

        /// <inheritdoc />
        public override ValueTask<PipeReader> GetPipeReaderAsync(string contentType, long position, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetPipeReaderInternal(contentType, position, cancellationToken));

        private PipeReader GetPipeReaderInternal(string contentType, long position, CancellationToken cancellationToken)
        {
            VerifyContentType(contentType);
            var sequence = new ReadOnlySequence<byte>(Body);
            var pipeReader = PipeReader.Create(sequence);
            if (position > 0)
                pipeReader.AdvanceTo(sequence.GetPosition(position));
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
            return ValueTask.FromResult(new ReadOnlySequence<byte>(Body));
        }

        /// <inheritdoc />
        public override ValueTask<IAsyncEnumerator<ReadOnlyMemory<byte>>> GetPayloadStreamAsync(string contentType, int position, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
