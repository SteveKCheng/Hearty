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
    /// Construct by de-serializing the MessagePack representation
    /// of <see cref="ExceptionPayload" />.
    /// </summary>
    public PromiseExceptionalData(ReadOnlySequence<byte> payloadMsgPack)
    {
        _payloadMsgPack = payloadMsgPack;
        _hasPayloadMsgPack = true;
        _payload = MessagePackSerializer.Deserialize<ExceptionPayload>(payloadMsgPack);
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

    private static ReadOnlySequence<byte> FormatAsText(ExceptionPayload payload)
    {
        var builder = new StringBuilder();
        builder.Append("Class: ");
        builder.AppendLine(payload.Class);
        builder.Append("Description: ");
        builder.AppendLine(payload.Description);
        builder.AppendLine("Stack Trace: ");
        builder.AppendLine(payload.Trace);
        return new ReadOnlySequence<byte>(Utf8NoBOM.GetBytes(builder.ToString()));
    }

    private static ReadOnlySequence<byte> FormatAsJson(ExceptionPayload payload)
    {
        var bufferWriter = new SegmentedArrayBufferWriter<byte>(
                        initialBufferSize: 2048, doublingThreshold: 4);
        var jsonWriter = new Utf8JsonWriter(bufferWriter);
        JsonSerializer.Serialize(jsonWriter, payload);
        jsonWriter.Flush();
        return bufferWriter.GetWrittenSequence();
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
            Format.MessagePack => MessagePackPayload,
            Format.Json => FormatAsJson(_payload),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

        return bytes;
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

    /// <summary>
    /// Cached MessagePack serialization of <see cref="_payload" />.
    /// </summary>
    private ReadOnlySequence<byte> _payloadMsgPack;

    /// <summary>
    /// Whether <see cref="_payloadMsgPack" /> is valid.
    /// </summary>
    private volatile bool _hasPayloadMsgPack;

    /// <summary>
    /// Get the MessagePack serialization of the exception.
    /// </summary>
    /// <remarks>
    /// This serialization is cached within this object.
    /// </remarks>
    internal ReadOnlySequence<byte> MessagePackPayload
    {
        get
        {
            if (_hasPayloadMsgPack)
                return _payloadMsgPack;

            lock (_payload)
            {
                if (_hasPayloadMsgPack)
                    return _payloadMsgPack;

                var writer = new SegmentedArrayBufferWriter<byte>(
                                initialBufferSize: 2048, doublingThreshold: 4);
                MessagePackSerializer.Serialize(writer, _payload);

                var payloadMsgPack = writer.GetWrittenSequence();
                _payloadMsgPack = payloadMsgPack;
                _hasPayloadMsgPack = true;

                return payloadMsgPack;
            }
        }
    }

    /// <inheritdoc />
    public override bool TryPrepareSerialization(out PromiseDataSerializationInfo info)
    {
        info = new PromiseDataSerializationInfo
        {
            PayloadLength = checked((int)MessagePackPayload.Length),
            SchemaCode = SchemaCode,
            State = this,
            Serializer = static (in PromiseDataSerializationInfo info, Span<byte> buffer)
                => ((PromiseExceptionalData)info.State!).Serialize(buffer)
        };

        return true;
    }

    private void Serialize(Span<byte> buffer)
    {
        var payload = MessagePackPayload;

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
    /// <param name="fixtures">
    /// Unused, but required for the function signature to conform
    /// to <see cref="PromiseDataDeserializer" />.
    /// </param>
    /// <param name="buffer">
    /// The buffer of bytes to de-serialize from.
    /// </param>
    /// <param name="inputData">
    /// Ignored but required as part of the signature of <see cref="PromiseDataDeserializer" />.
    /// </param>
    public static PromiseExceptionalData Deserialize(IPromiseDataFixtures fixtures,
                                                     ReadOnlySpan<byte> buffer,
                                                     PromiseData? inputData)
    {
        // Need to copy the buffer since we save the MessagePack serialization
        // inside the new object.
        var bytes = new byte[buffer.Length];
        buffer.CopyTo(bytes);

        return new PromiseExceptionalData(new ReadOnlySequence<byte>(bytes));
    }
}
