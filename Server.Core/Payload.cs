using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Common;
using Hearty.Utilities;

namespace Hearty.Server;

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

    private enum Flags : int
    {
        Normal = 0,
        IsFailure = 1
    }

    /// <inheritdoc />
    public override bool TryGetSerializationInfo(out PromiseDataSerializationInfo info)
    {
        var payloadLength = sizeof(int) + (uint)_contentType.Input.Length
                            + sizeof(Flags)
                            + sizeof(int) + (ulong)Body.Length;

        if (payloadLength > Int32.MaxValue)
        {
            info = default;
            return false;
        }

        info = new PromiseDataSerializationInfo
        {
            PayloadLength = (int)payloadLength,
            SchemaCode = SchemaCode
        };

        return true;
    }

    /// <inheritdoc />
    public override void Serialize(Span<byte> buffer)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, _contentType.Input.Length);
        buffer = buffer[sizeof(int)..];

        Encoding.ASCII.GetBytes(_contentType.Input, buffer);
        buffer = buffer[_contentType.Input.Length..];

        var flags = IsFailure ? Flags.IsFailure : Flags.Normal;
        BinaryPrimitives.WriteInt32LittleEndian(buffer, (int)flags);

        BinaryPrimitives.WriteInt32LittleEndian(buffer, (int)Body.Length);
        buffer = buffer[sizeof(int)..];

        Body.CopyTo(buffer);
    }

    /// <summary>
    /// De-serialize an instance of <see cref="Payload" />
    /// from its serialization created from <see cref="Serialize" />.
    /// </summary>
    /// <param name="buffer">
    /// The buffer of bytes to de-serialize from.
    /// </param>
    public static Payload Deserialize(ReadOnlySpan<byte> buffer)
    {
        int contentTypeStringLength = BinaryPrimitives.ReadInt32BigEndian(buffer);
        buffer = buffer[sizeof(int)..];

        var contentTypeString = Encoding.ASCII.GetString(buffer[0..contentTypeStringLength]);
        buffer = buffer[contentTypeStringLength..];

        var flags = (Flags)BinaryPrimitives.ReadInt32LittleEndian(buffer);
        buffer = buffer[sizeof(int)..];

        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        buffer = buffer[sizeof(int)..];

        var body = new byte[bodyLength];
        buffer[0..bodyLength].CopyTo(body);

        bool isFailure = flags switch
        {
            Flags.Normal => false,
            Flags.IsFailure => true,
            _ => throw new InvalidDataException()
        };

        return new Payload(new ParsedContentType(contentTypeString),
                           new ReadOnlySequence<byte>(body),
                           isFailure);
    }

    /// <summary>
    /// Schema code for serialization.
    /// </summary>
    public const ushort SchemaCode = (byte)'P' | ((byte)'a' << 8);
}
