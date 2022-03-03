using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Common;
using Hearty.Utilities;

namespace Hearty.Server
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
        private readonly ParsedContentType _contentType;

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
        /// <param name="contentType">IANA media/content type for the body.
        /// If this type is invalid (has incorrect syntax), it is silently
        /// replaced by <see cref="ServedMediaTypes.OctetStream" />.
        /// </param>
        /// <param name="body">Sequence of bytes, possibly in multiple buffers chained together. </param>
        /// <param name="isFailure">Whether this payload encapsulates a failure condition,
        /// as defined by <see cref="PromiseData.IsFailure" />.
        /// </param>
        public Payload(ParsedContentType contentType, in ReadOnlySequence<byte> body, bool isFailure = false)
        {
            if (!contentType.IsValid)
                contentType = ServedMediaTypes.OctetStream;

            _contentType = contentType;
            Body = body;
            IsFailure = isFailure;
        }

        /// <summary>
        /// Provide a sequence of bytes in one contiguous memory buffer, 
        /// labelled with a fixed content type.
        /// </summary>
        /// <param name="contentType">IANA media/content type for the body.
        /// If this type is invalid (has incorrect syntax), it is silently
        /// replaced by <see cref="ServedMediaTypes.OctetStream" />.
        /// </param>
        /// <param name="body">Sequence of bytes in one contiguous member buffer. </param>
        /// <param name="isFailure">Whether this payload encapsulates a failure condition,
        /// as defined by <see cref="PromiseData.IsFailure" />.
        /// </param>
        public Payload(ParsedContentType contentType, in ReadOnlyMemory<byte> body, bool isFailure = false)
            : this(contentType, new ReadOnlySequence<byte>(body), isFailure)
        {
        }

        /// <inheritdoc />
        public override ValueTask WriteToPipeAsync(PipeWriter writer, PromiseWriteRequest request, CancellationToken cancellationToken)
        {
            return writer.WriteAsync(Body.Slice(request.Start), cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask<Stream> GetByteStreamAsync(int format, CancellationToken cancellationToken)
        {
            Stream stream = new MemoryReadingStream(Body);
            return ValueTask.FromResult(stream);
        }

        /// <inheritdoc />
        public override ValueTask<ReadOnlySequence<byte>> GetPayloadAsync(int format, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Body);
        }

        /// <inheritdoc />
        public override ContentFormatInfo GetFormatInfo(int format)
            => new(_contentType,
                   ContentPreference.Best,
                   ContentSeekability.Bytes);

        /// <inheritdoc />
        public override long? GetContentLength(int format) => Body.Length;

        /// <summary>
        /// Transformer for the output from a remotely-run job into 
        /// a newly created instance of this class.
        /// </summary>
        public static Func<object, SimplePayload, ValueTask<PromiseData>>
            JobOutputDeserializer { get; } = DeserializeJobOutput;

        /// <summary>
        /// Transformer from <see cref="PromiseData" /> 
        /// to the input for a job to be remotely run.
        /// </summary>
        public static Func<object, ValueTask<SimplePayload>>
            JobInputSerializer { get; } = SerializeJobInput;

        private static ValueTask<PromiseData> DeserializeJobOutput(object _, SimplePayload p)
        {
            PromiseData d = new Payload(p.ContentType, p.Body);
            return ValueTask.FromResult(d);
        }

        private static async ValueTask<SimplePayload> SerializeJobInput(object state)
        {
            var promiseData = (PromiseData)state;
            var contentType = promiseData.ContentType;
            var body = await promiseData.GetPayloadAsync(format: 0, default)
                                        .ConfigureAwait(false);
            return new(contentType, body);
        }
    }
}
