using Hearty.Common;
using Hearty.Utilities;
using MessagePack;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Represents an exception (from job execution) as the
/// output of a promise.
/// </summary>
public sealed class PromiseExceptionalData : PromiseData
{
    private readonly ExceptionPayload _payload;

    /// <summary>
    /// Encodes strings to UTF-8 without the so-called "byte order mark".
    /// </summary>
    private static readonly Encoding Utf8NoBOM = new UTF8Encoding(false);

    /// <summary>
    /// Wrap <see cref="ExceptionPayload" /> to be usable as
    /// promise output.
    /// </summary>
    public PromiseExceptionalData(ExceptionPayload payload)
    {
        _payload = payload;
    }

    /// <summary>
    /// Convert a .NET exception to a form usable as promise output.
    /// </summary>
    public PromiseExceptionalData(Exception exception)
        : this(ExceptionPayload.CreateFromException(exception))
    {
    }

    /// <summary>
    /// Always returns true on this class: it represents a failure.
    /// </summary>
    public override bool IsFailure => true;

    public override bool IsTransient => IsCancellation;

    /// <summary>
    /// Whether the exception is from cancelling an operation.
    /// </summary>
    public bool IsCancellation => _payload.Cancelling;

    private enum Format
    {
        Json = 0,
        MessagePack = 1,
        Text = 2,
        Count
    }

    private static byte[] FormatAsText(ExceptionPayload payload)
    {
        var builder = new StringBuilder();
        builder.Append("Class: ");
        builder.AppendLine(payload.Class);
        builder.Append("Description: ");
        builder.AppendLine(payload.Description);
        builder.AppendLine("Stack Trace: ");
        builder.AppendLine(payload.Trace);
        return Utf8NoBOM.GetBytes(builder.ToString());
    }

    /// <inheritdoc />
    public override ValueTask<Stream> GetByteStreamAsync(int format, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = GetPayload(format);
        Stream stream = new MemoryReadingStream(payload);
        return ValueTask.FromResult(stream);
    }

    /// <summary>
    /// Get the exception serialized into buffers of bytes.
    /// </summary>
    /// <param name="format">
    /// The desired format of the data to present.  Must 
    /// be in the range of 0 to <see cref="CountFormats" /> minus one.
    /// </param>
    /// <returns>
    /// The payload as a sequence of blocks of bytes,
    /// which may in fact be what is internally stored
    /// by the implementation.  The result is the same as what
    /// is wrapped by <see cref="GetPayloadAsync" />; for this
    /// implementation the result is always available synchronously.
    /// </returns>
    public ReadOnlySequence<byte> GetPayload(int format)
    {
        var bytes = (Format)format switch
        {
            Format.Text => FormatAsText(_payload),
            Format.MessagePack => MessagePackSerializer.Serialize(_payload),
            Format.Json => JsonSerializer.SerializeToUtf8Bytes(_payload),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

        return new ReadOnlySequence<byte>(bytes);
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
        var payload = GetPayload(request.Format).Slice(request.Start);
        return writer.WriteAsync(payload, cancellationToken);
    }

    /// <inheritdoc />
    public override int CountFormats => (int)Format.Count;

    /// <inheritdoc />
    public override ContentFormatInfo GetFormatInfo(int format)
        => (Format)format switch
        {
            Format.Text => new(ServedMediaTypes.TextPlain, ContentPreference.Fair),
            Format.Json => new(ServedMediaTypes.ExceptionJson, ContentPreference.Good),
            Format.MessagePack => new(ServedMediaTypes.ExceptionMsgPack, ContentPreference.Best),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    /// <inheritdoc />
    public override bool TryPrepareSerialization(out PromiseDataSerializationInfo info)
    {
        ReadOnlySequence<byte> payload = GetPayload((int)Format.MessagePack);

        info = new PromiseDataSerializationInfo
        {
            PayloadLength = checked((int)payload.Length),
            SchemaCode = SchemaCode,
            State = payload,
            Serializer = static (in PromiseDataSerializationInfo info, Span<byte> buffer)
                => Serialize((ReadOnlySequence<byte>)info.State!, buffer)
        };

        return true;
    }

    private static void Serialize(ReadOnlySequence<byte> payload, 
                                  Span<byte> buffer)
    {
        while (payload.Length > 0)
        {
            var segment = payload.FirstSpan;
            segment.CopyTo(buffer);
            buffer = buffer[segment.Length..];
            payload = payload.Slice(segment.Length);
        }
    }

    /// <summary>
    /// Schema code for serialization.
    /// </summary>
    public const ushort SchemaCode = (byte)'E' | ((byte)'x' << 8);

    /// <summary>
    /// De-serialize an instance of this class
    /// from its serialization created from <see cref="Serialize" />.
    /// </summary>
    /// <param name="buffer">
    /// The buffer of bytes to de-serialize from.
    /// </param>
    public static PromiseExceptionalData Deserialize(ReadOnlySpan<byte> buffer)
    {
        // Need to copy because MessagePackSerializer does not support spans,
        // even though it ought to because MessagePackReader is a ref struct
        // anyway.  Unsafe code could work around this problem.  Exception
        // payload is expected to be small so the copy is not so painful.
        var copy = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(copy);

            var reader = new MessagePackReader(copy);
            var body = MessagePackSerializer.Deserialize<ExceptionPayload>(ref reader);
            return new PromiseExceptionalData(body);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copy);
        }
    }
}
