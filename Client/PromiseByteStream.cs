using Hearty.Common;
using Microsoft.Extensions.Primitives;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Client;

/// <summary>
/// The result of a promise exposed as a byte stream.
/// </summary>
public readonly struct PromiseByteStream : IDisposable
{
    /// <summary>
    /// The ID of the promise on the Hearty server providing this output.
    /// </summary>
    public PromiseId PromiseId { get; }

    /// <summary>
    /// The IANA media type of the promise output.
    /// </summary>
    public ParsedContentType ContentType { get; }

    /// <summary>
    /// Stream providing the raw bytes of the promise output.
    /// </summary>
    public Stream Stream { get; }

    /// <summary>
    /// Closes the stream of the promise output.
    /// </summary>
    /// <remarks>
    /// Closing the stream entails disposing the
    /// resource or connection that had been opened
    /// for querying the promise output.
    /// </remarks>
    public void Dispose()
    {
        Stream.Dispose();
    }

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="promiseId">
    /// The ID of the promise providing this output.
    /// </param>
    /// <param name="contentType">
    /// The IANA media type of the promise output.
    /// </param>
    /// <param name="stream">
    /// Stream providing the raw bytes of the promise output.
    /// </param>
    public PromiseByteStream(PromiseId promiseId,
                             ParsedContentType contentType, 
                             Stream stream)
    {
        PromiseId = promiseId;
        ContentType = contentType;
        Stream = stream;
    }

    internal static PayloadReader<PromiseByteStream> 
        GetPayloadReader(StringValues contentTypes, bool throwOnException)
        => new(contentTypes,
               static (p, t, s, c) => ReadAsync(p, t, s, c),
               throwOnException, 
               ownsStream: true);

    private static ValueTask<PromiseByteStream> 
        ReadAsync(PromiseId promiseId,
                  ParsedContentType contentType,
                  Stream stream,
                  CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new PromiseByteStream(promiseId, contentType, stream);
        return ValueTask.FromResult(result);
    }
}
