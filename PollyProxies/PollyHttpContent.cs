using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Polly;

namespace Hearty.PollyProxies;

/// <summary>
/// Wraps the body of a HTTP request or response so that reading
/// or writing from it goes through the Polly resilience framework. 
/// </summary>
public sealed class PollyHttpContent : HttpContent
{
    private readonly HttpContent _source;
    private readonly ISyncPolicy _syncPolicy;
    private readonly IAsyncPolicy _asyncPolicy;
    private readonly Context _pollyContext;

    /// <summary>
    /// Wrap some instance of <see cref="HttpContent" /> so that
    /// reading or writing from it goes through Polly. 
    /// </summary>
    /// <param name="source">The original HTTP content. </param>
    /// <param name="syncPolicy">
    /// Execution policy under Polly for synchronous I/O methods.
    /// </param>
    /// <param name="asyncPolicy">
    /// Execution policy under Polly for asynchronous I/O methods.
    /// </param>
    /// <param name="pollyContext">
    /// Context object to pass to Polly.
    /// </param>
    public PollyHttpContent(HttpContent source, 
                            ISyncPolicy syncPolicy, 
                            IAsyncPolicy asyncPolicy, 
                            Context pollyContext)
    {
        _source = source;
        _syncPolicy = syncPolicy;
        _asyncPolicy = asyncPolicy;
        _pollyContext = pollyContext;

        PollyHttpMessageHandler.CopyHeaders(source.Headers, this.Headers);
    }

    //
    // FIXME should these methods be faulting?
    //

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => _source.CopyToAsync(stream, context);

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => _source.CopyToAsync(stream, context, cancellationToken);

    /// <inheritdoc />
    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => _source.CopyTo(stream, context, cancellationToken);


    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        // FIXME: not sure how to implement
        length = default;
        return false;
    }

    /// <inheritdoc />
    protected override Task<Stream> CreateContentReadStreamAsync()
        => CreateContentReadStreamAsync(CancellationToken.None);

    /// <inheritdoc />
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        => new PollyStream(await _source.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                           _syncPolicy, _asyncPolicy, _pollyContext);

    /// <inheritdoc />
    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        => new PollyStream(_source.ReadAsStream(cancellationToken),
                           _syncPolicy, _asyncPolicy, _pollyContext);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
        => _source.Dispose();
}
