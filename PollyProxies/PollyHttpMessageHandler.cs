using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Polly;

namespace Hearty.PollyProxies;

/// <summary>
/// Inject a Polly policies into a HTTP message stack.
/// </summary>
public sealed class PollyHttpMessageHandler : DelegatingHandler
{
    private readonly ISyncPolicy _syncPolicyForSend;
    private readonly IAsyncPolicy _asyncPolicyForSend;
    private readonly ISyncPolicy _syncPolicyForStream;
    private readonly IAsyncPolicy _asyncPolicyForStream;

    public PollyHttpMessageHandler(HttpMessageHandler innerHandler, 
                                   ISyncPolicy syncPolicyForSend,
                                   IAsyncPolicy asyncPolicyForSend,
                                   ISyncPolicy syncPolicyForBody,
                                   IAsyncPolicy asyncPolicyForBody)
        : base(innerHandler)
    {
        _syncPolicyForSend = syncPolicyForSend;
        _asyncPolicyForSend = asyncPolicyForSend;
        _syncPolicyForStream = syncPolicyForBody;
        _asyncPolicyForStream = asyncPolicyForBody;
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = request.GetPolicyExecutionContext() ?? new Context();
        var response = _syncPolicyForSend.Execute(context => base.Send(request, cancellationToken), context);
        return WrapResponse(response, context);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = request.GetPolicyExecutionContext() ?? new Context();
        var response = await _asyncPolicyForSend.ExecuteAsync(context => base.SendAsync(request, cancellationToken),
                                                              context)
                                                .ConfigureAwait(false);
        return WrapResponse(response, context);
    }

    private HttpResponseMessage WrapResponse(HttpResponseMessage original, Context context)
    {
        // Note: do not dispose the original response
        //       since we need to retain the source content

        var response = new HttpResponseMessage()
        {
            Version = original.Version,
            StatusCode = original.StatusCode,
            ReasonPhrase = original.ReasonPhrase,
            RequestMessage = original.RequestMessage,
            Content = new PollyHttpContent(original.Content, 
                                           _syncPolicyForStream, 
                                           _asyncPolicyForStream, 
                                           context)
        };

        CopyHeaders(original.Headers, response.Headers);
        CopyHeaders(original.TrailingHeaders, response.TrailingHeaders);

        return response;
    }

    internal static void CopyHeaders(HttpHeaders source, HttpHeaders destination)
    {
        foreach (var (key, value) in source)
            destination.TryAddWithoutValidation(key, value);
    }
}
