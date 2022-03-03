using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Hearty.Client;

/// <summary>
/// Encapsulates a payload to upload to a Hearty server.
/// </summary>
/// <remarks>
/// <para>
/// This structure abstracts out the representation of a payload
/// for a Hearty server, from the implementation of the 
/// underlying protocol.
/// </para>
/// <para>
/// An application may pre-construct instances of this type,
/// one for each type of input object that it wishes to send
/// to the Hearty server.
/// </para>
/// </remarks>
public readonly struct PayloadWriter
{
    /// <summary>
    /// The IANA media type for the payload.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// The length of the payload in bytes, if known upfront.
    /// </summary>
    public long? ContentLength
    {
        get
        {
            var len = _contentLength;
            return len >= 0 ? len : null;
        }
    }

    private readonly ReadOnlySequence<byte> _body;
    private readonly long _contentLength;
    private readonly Func<Stream, Task>? _streamWriter;

    /// <summary>
    /// Prepare an in-memory payload to upload.
    /// </summary>
    /// <param name="contentType">The IANA media type for the payload. </param>
    /// <param name="body">The bytes of the payload in a sequence
    /// of buffers. </param>
    public PayloadWriter(string contentType, in ReadOnlySequence<byte> body)
    {
        HeartyHttpClient.VerifyContentTypeSyntax(contentType);

        ContentType = contentType;
        _body = body;
        _contentLength = body.Length;
        _streamWriter = null;
    }

    /// <summary>
    /// Prepare an in-memory payload to upload.
    /// </summary>
    /// <param name="contentType">The IANA media type for the payload. </param>
    /// <param name="body">The bytes of the payload. </param>
    public PayloadWriter(string contentType, in ReadOnlyMemory<byte> body)
        : this(contentType, new ReadOnlySequence<byte>(body))
    {
    }

    /// <summary>
    /// Prepare an in-memory payload to upload.
    /// </summary>
    /// <param name="contentType">The IANA media type for the payload. </param>
    /// <param name="body">The bytes of the payload. </param>
    public PayloadWriter(string contentType, byte[] body)
        : this(contentType, new ReadOnlySequence<byte>(body))
    {
    }

    /// <summary>
    /// Prepare to upload a payload that is generated incrementally.
    /// </summary>
    /// <param name="contentType">The IANA media type for the payload. </param>
    /// <param name="contentLength">The size of the payload in bytes,
    /// if known. </param>
    /// <param name="streamWriter">
    /// This asynchronous function is invoked to write the payload
    /// into the supplied stream.
    /// </param>
    public PayloadWriter(string contentType,
                         long? contentLength,
                         Func<Stream, Task> streamWriter)
    {
        HeartyHttpClient.VerifyContentTypeSyntax(contentType);

        if (contentLength is long value && value < 0)
            throw new ArgumentOutOfRangeException(nameof(contentLength));

        ContentType = contentType;
        _body = default;
        _contentLength = contentLength ?? -1;
        _streamWriter = streamWriter ?? throw new ArgumentNullException(nameof(streamWriter));
    }

    /// <summary>
    /// Prepare to upload a payload that is generated incrementally.
    /// </summary>
    /// <param name="contentType">The IANA media type for the payload. </param>
    /// <param name="streamWriter">
    /// This asynchronous function is invoked to write the payload
    /// into the supplied stream.
    /// </param>
    public PayloadWriter(string contentType,
                         Func<Stream, Task> streamWriter)
        : this(contentType, null, streamWriter)
    {
    }

    /// <summary>
    /// Create an instance of <see cref="HttpContent" /> that can
    /// upload the payload as part of a HTTP request.
    /// </summary>
    internal HttpContent CreateHttpContent()
    {
        if (_streamWriter is not null)
            return new StreamWriterHttpContent(ContentType, ContentLength, _streamWriter);
        else
            return new ByteSequenceHttpContent(ContentType, _body);
    }
}

internal sealed class StreamWriterHttpContent : HttpContent
{
    private readonly Func<Stream, Task> _writer;
    private readonly long? _contentLength;

    public StreamWriterHttpContent(string contentType, long? contentLength, Func<Stream, Task> writer)
    {
        Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);
        _contentLength = contentLength;
        _writer = writer;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => _writer.Invoke(stream);

    protected override bool TryComputeLength(out long length)
    {
        if (_contentLength is long value)
        {
            length = value;
            return true;
        }
        else
        {
            length = default;
            return false;
        }
    }
}

internal sealed class ByteSequenceHttpContent : HttpContent
{
    private readonly ReadOnlySequence<byte> _body;

    public ByteSequenceHttpContent(string contentType, in ReadOnlySequence<byte> body)
    {
        Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);
        _body = body;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var body = _body;
        while (body.Length > 0)
        {
            var segment = body.First;
            await stream.WriteAsync(segment).ConfigureAwait(false);
            body = body.Slice(segment.Length);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _body.Length;
        return true;
    }
}
