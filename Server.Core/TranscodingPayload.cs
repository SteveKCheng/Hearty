using Hearty.Utilities;
using System;
using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Describes a format that can be transcoded by <see cref="TranscodingPayload{T}" />.
/// </summary>
/// <typeparam name="T">
/// Type for the internal representation of the source data.
/// </typeparam>
public readonly struct TranscodedFormatInfo<T>
{
    /// <summary>
    /// Describes the content format to clients.
    /// </summary>
    public ContentFormatInfo FormatInfo { get; }

    /// <summary>
    /// Function that <see cref="TranscodingPayload{T}" /> invokes
    /// to translate from <typeparamref name="T" />
    /// to the format described in <see cref="FormatInfo" />.
    /// </summary>
    public Func<T, ReadOnlySequence<byte>> Transcoding { get; }

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="formatInfo">Sets <see cref="FormatInfo" />. </param>
    /// <param name="transcoding">Sets <see cref="Transcoding" />. </param>
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

    /// <summary>
    /// Store the source data (in memory) and prepare to transcode it
    /// for clients.
    /// </summary>
    /// <param name="data">The internal representation of the source data. </param>
    /// <param name="spec">
    /// Describes all the formats that the source data can be transcoded
    /// to and made available to clients.
    /// </param>
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
