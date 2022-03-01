using Hearty.Utilities;
using System;
using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server
{
    public readonly struct TranscodedFormatInfo<T>
    {
        public ContentFormatInfo FormatInfo { get; }

        public Func<T, ReadOnlySequence<byte>> Transcoding { get; }

        public TranscodedFormatInfo(ContentFormatInfo formatInfo,
                                    Func<T, ReadOnlySequence<byte>> transcoding)
        {
            FormatInfo = formatInfo;
            Transcoding = transcoding;
        }
    }

    /// <summary>
    /// Promise output that can be made available in one of multiple formats.
    /// </summary>
    /// <typeparam name="T">
    /// The intrinsic or internal representation of the data that
    /// can be transcoded to one of multiple formats.
    /// </typeparam>
    public sealed class TranscodingPayload<T> : PromiseData
    {
        private readonly ImmutableArray<TranscodedFormatInfo<T>> _spec;
        private readonly T _data;

        public TranscodingPayload(T data, ImmutableArray<TranscodedFormatInfo<T>> spec)
        {
            _data = data;
            _spec = spec;
        }

        /// <inheritdoc />
        public override ValueTask<Stream> GetByteStreamAsync(int format, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = GetPayload(format);
            Stream stream = new MemoryReadingStream(payload);
            return ValueTask.FromResult(stream);
        }

        /// <inheritdoc />
        public override ContentFormatInfo GetFormatInfo(int format)
        {
            return _spec[format].FormatInfo;
        }

        /// <inheritdoc />
        public override int CountFormats => _spec.Length;

        private ReadOnlySequence<byte> GetPayload(int format)
        {
            return _spec[format].Transcoding.Invoke(_data);
        }

        /// <inheritdoc />
        public override ValueTask<ReadOnlySequence<byte>> GetPayloadAsync(int format, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetPayload(format));
        }

        /// <inheritdoc />
        public override ValueTask WriteToPipeAsync(PipeWriter writer, PromiseWriteRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = GetPayload(request.Format);
            return writer.WriteAsync(payload.Slice(request.Start), cancellationToken);
        }
    }
}
