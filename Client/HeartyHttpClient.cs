using Hearty.Common;
using Microsoft.Extensions.Primitives;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Hearty.Client;

/// <summary>
/// Strongly-typed access to interact with a
/// a Hearty server over HTTP.
/// </summary>
public partial class HeartyHttpClient : IHeartyClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _leaveOpen;
    private readonly string _serverUrl;
    private Action<HeartyHttpClient, HttpRequestMessage>? _messageTracer;

    /// <summary>
    /// Wrap a strongly-typed interface for Hearty over 
    /// a standard HTTP client.
    /// </summary>
    /// <param name="httpClient">
    /// Existing HTTP client instance.
    /// </param>
    /// <param name="serverUrl">
    /// The HTTP URL to the Hearty server. 
    /// If null, the URL will be taken from 
    /// <see cref="HttpClient.BaseAddress" />
    /// which must then be set.
    /// This URL may be relative to 
    /// <see cref="HttpClient.BaseAddress" />.
    /// </param>
    /// <param name="leaveOpen">
    /// If true, do not dispose <paramref name="httpClient" />
    /// on disposing this instance.
    /// </param>
    /// <param name="messageTracer">
    /// An optional action hooking into the creation
    /// of HTTP messages from this object, possibly modifying it 
    /// before it gets sent.  Such an action may be used to 
    /// log all messages created, or attach contextual information
    /// (via <see cref="HttpRequestMessage.Options" />) for
    /// the implementations of <see cref="HttpMessageHandler" /> inside
    /// <paramref name="httpClient" /> to process.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpClient" /> is null,
    /// or <paramref name="serverUrl" /> 
    /// and
    /// <see cref="HttpClient.BaseAddress" /> 
    /// on <paramref name="httpClient" /> are both null.
    /// </exception>
    public HeartyHttpClient(HttpClient httpClient,
                            string? serverUrl = null,
                            bool leaveOpen = false,
                            Action<HeartyHttpClient, HttpRequestMessage>? messageTracer = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _leaveOpen = leaveOpen;
        _messageTracer = messageTracer;

        // We assume this is an absolute URI if non-null, which
        // is ensured by the property setter of HttpClient.BaseAddress.
        Uri? baseAddress = httpClient.BaseAddress;

        if (Uri.IsWellFormedUriString(serverUrl, UriKind.Absolute))
            _serverUrl = serverUrl;
        else if (baseAddress is not null && serverUrl is null)
            _serverUrl = baseAddress.AbsoluteUri;
        else if (baseAddress is not null && 
                 Uri.IsWellFormedUriString(serverUrl, UriKind.Relative))
            _serverUrl = new Uri(baseAddress, serverUrl).AbsoluteUri;
        else if (serverUrl is null)
            throw new ArgumentNullException(
                        nameof(serverUrl),
                        "The server URL must be specified " +
                        "since it is not present in HttpClient.BaseAddress. ");
        else
            throw new ArgumentException("The server URL is invalid. ", nameof(serverUrl));
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        if (!_leaveOpen)
            _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void EnsureSuccessStatusCodeEx(HttpResponseMessage response)
    {
        int statusCode = (int)response.StatusCode;
        if (statusCode >= 300 && statusCode < 400)
            return;

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc cref="IHeartyClient.PostJobAsync" />
    public async Task<PromiseId> PostJobAsync(string route,
                                              PayloadWriter input,
                                              JobQueueKey queue = default,
                                              CancellationToken cancellationToken = default)
    {
        var url = CreateRequestUrl("requests/",
                                   route: route,
                                   queue: queue);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = input.CreateHttpContent()
        };

        var response = await SendHttpMessageAsync(request, true, cancellationToken)
                                        .ConfigureAwait(false);
        EnsureSuccessStatusCodeEx(response);

        return GetPromiseId(response.Headers);
    }

    private readonly struct TimeSpanFormatWrapper : ISpanFormattable
    {
        public readonly TimeSpan TimeSpan;

        public TimeSpanFormatWrapper(TimeSpan timeSpan) => TimeSpan = timeSpan;

        public string ToString(string? format, IFormatProvider? formatProvider)
            => RestApiUtilities.FormatExpiry(TimeSpan);

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => RestApiUtilities.TryFormatExpiry(TimeSpan, destination, out charsWritten);
    }

    private Task<HttpResponseMessage> SendHttpMessageAsync(HttpRequestMessage request, bool bufferBody, CancellationToken cancellationToken)
    {
        _messageTracer?.Invoke(this, request);
        return _httpClient.SendAsync(request,
                                     bufferBody ? HttpCompletionOption.ResponseContentRead
                                                : HttpCompletionOption.ResponseHeadersRead,
                                     cancellationToken);
    }

    private string CreateRequestUrl(string path,
                                    PromiseId? promiseId = null,
                                    string? route = null,
                                    TimeSpan? timeout = null,
                                    bool? wantResult = null,
                                    JobQueueKey queue = default)
    {
        var builder = new ValueStringBuilder(stackalloc char[1024]);

        builder.Append(_serverUrl);
        if (!_serverUrl.EndsWith('/'))
            builder.Append('/');

        builder.Append(path);

        if (route is not null)
        {
            if (!path.EndsWith('/'))
                builder.Append('/');
            builder.Append(route);
        }

        if (promiseId != null)
        {
            if (!path.EndsWith('/'))
                builder.Append('/');
            builder.Append(promiseId.Value);
        }

        static void AppendQuerySeparator(ref ValueStringBuilder builder, ref int paramCount)
        {
            bool isFirst = (paramCount++ == 0);
            builder.Append(isFirst ? '?' : '&');
        }

        int paramCount = 0;

        if (timeout is not null)
        {
            AppendQuerySeparator(ref builder, ref paramCount);
            builder.Append("timeout=");
            builder.Append(new TimeSpanFormatWrapper(timeout.Value));
        }

        if (wantResult is not null)
        {
            AppendQuerySeparator(ref builder, ref paramCount);
            builder.Append("result=");
            builder.Append(wantResult.Value ? "true" : "false");
        }

        if (queue.Owner is String owner)
        {
            AppendQuerySeparator(ref builder, ref paramCount);
            builder.Append("owner=");
            builder.Append(Uri.EscapeDataString(owner));
        }

        if (queue.Cohort is string cohort)
        {
            AppendQuerySeparator(ref builder, ref paramCount);
            builder.Append("cohort=");
            builder.Append(Uri.EscapeDataString(cohort));
        }

        if (queue.Priority is int priority)
        {
            AppendQuerySeparator(ref builder, ref paramCount);
            builder.Append("priority=");
            builder.Append(priority);
        }

        return builder.ToString();
    }

    /// <inheritdoc cref="IHeartyClient.RunJobAsync" />
    public async Task<T> RunJobAsync<T>(string route,
                                        PayloadWriter input,
                                        PayloadReader<T> reader,
                                        JobQueueKey queue = default,
                                        CancellationToken cancellationToken = default)
    {
        var url = CreateRequestUrl("requests/",
                                   route: route,
                                   wantResult: true,
                                   queue: queue);
        var message = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuthorizationHeader(message);
        reader.AddAcceptHeaders(message.Headers);
        message.Headers.TryAddWithoutValidation(HeaderNames.Accept, ExceptionPayload.JsonMediaType);
        message.Content = input.CreateHttpContent();

        var response = await SendHttpMessageAsync(message, false, cancellationToken)
                                .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = response.Content;

        var actualContentType = content.Headers.TryGetSingleValue(HeaderNames.ContentType)
            ?? throw new InvalidDataException("The Content-Type returned in the response is unexpected. ");

        var promiseId = GetPromiseId(content.Headers);

        using var stream = await content.ReadAsStreamAsync(cancellationToken)
                                        .ConfigureAwait(false);

        T result = await reader.ReadFromStreamAsync(promiseId,
                                                    new ParsedContentType(actualContentType),
                                                    stream,
                                                    cancellationToken)
                               .ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Extract the ID of the promise/job reported in the server's HTTP response.
    /// </summary>
    private static PromiseId GetPromiseId(HttpHeaders headers)
    {
        var promiseIdStr = headers.TryGetSingleValue(HeartyHttpHeaders.PromiseId);

        if (promiseIdStr is null)
        {
            throw new InvalidDataException(
                "The server did not report a promise ID for the job posting as expected. ");
        }

        if (!PromiseId.TryParse(promiseIdStr.Trim(), out var promiseId))
        {
            throw new InvalidDataException(
                "The promise ID reported by the server is invalid. ");
        }

        return promiseId;
    }

    /// <inheritdoc cref="IHeartyClient.GetPayloadAsync" />
    public Task<PromiseByteStream>
        GetPayloadAsync(PromiseId promiseId,
                        StringValues contentTypes,
                        TimeSpan timeout,
                        bool throwOnException = true,
                        CancellationToken cancellationToken = default)
    {
        var reader = PromiseByteStream.GetPayloadReader(contentTypes, throwOnException);
        return GetResultAsync(promiseId, reader, timeout, cancellationToken);
    }

    /// <inheritdoc cref="IHeartyClient.GetResultAsync" />
    public async Task<T> GetResultAsync<T>(PromiseId promiseId,
                                           PayloadReader<T> reader,
                                           TimeSpan timeout,
                                           CancellationToken cancellationToken = default)
    {
        var url = CreateRequestUrl("promises/", promiseId,
                                   timeout: (timeout != TimeSpan.Zero) ? timeout : null);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request);
        reader.AddAcceptHeaders(request.Headers);

        var response = await SendHttpMessageAsync(request, false, cancellationToken)
                                .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var content = response.Content;
        var contentType = new ParsedContentType(content.Headers.GetSingleValue(HeaderNames.ContentType));
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await reader.ReadFromStreamAsync(promiseId, contentType, stream, cancellationToken);
        }
        finally
        {
            if (!reader.OwnsStream)
                stream.Dispose();
        }
    }

    private const string MultipartParallelMediaType = "multipart/parallel";

    internal static void VerifyContentTypeSyntax(string contentType)
    {
        if (!new ParsedContentType(contentType).IsValid)
            throw new FormatException("The content type to accept for the job output is invalid. ");
    }

    private static bool VerifyContentType(ParsedContentType parsedActual, string expected)
    {
        var parsedExpected = new ParsedContentType(expected);
        return parsedActual.IsSubsetOf(parsedExpected);
    }

    private static bool VerifyContentType(string? actual, string expected)
    {
        if (actual is null)
            return true;

        return VerifyContentType(new ParsedContentType(actual), expected);
    }

    private static bool VerifyContentType(HttpContent content, string expected)
    {
        var actual = content.Headers.TryGetSingleValue(HeaderNames.ContentType);
        return VerifyContentType(actual, expected);
    }

    #region Job cancellation

    /// <inheritdoc cref="IHeartyClient.CancelJobAsync" />
    public async Task CancelJobAsync(PromiseId promiseId,
                                     JobQueueKey queue = default)
    {
        var url = CreateRequestUrl("promises/",
                                   promiseId,
                                   queue: queue);

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        AddAuthorizationHeader(request);

        var response = await SendHttpMessageAsync(request, true, CancellationToken.None)
                                .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc cref="IHeartyClient.KillJobAsync" />
    public async Task KillJobAsync(PromiseId promiseId)
    {
        var url = CreateRequestUrl("jobs/", promiseId);

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        AddAuthorizationHeader(request);

        var response = await SendHttpMessageAsync(request, true, CancellationToken.None)
                                    .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    #endregion

    #region Authentication

    /// <summary>
    /// Encode the payload for "basic authentication" in HTTP:
    /// the base64 encoding of user name and password
    /// separated by a colon.
    /// </summary>
    private static string EncodeBasicAuthentication(string user, string password)
    {
        int len = user.Length + password.Length;
        if (len > short.MaxValue)
            throw new InvalidOperationException("User name and/or password is too long. ");

        Span<byte> buffer = stackalloc byte[len * 4 + 1];

        int n = Encoding.UTF8.GetBytes(user.AsSpan(), buffer);
        buffer[n++] = (byte)':';
        n += Encoding.UTF8.GetBytes(password.AsSpan(), buffer[n..]);

        return Convert.ToBase64String(buffer[0..n]);
    }

    /// <summary>
    /// Add the authorization header for the bearer token,
    /// which must have been set, to a HTTP request message.
    /// </summary>
    private void AddAuthorizationHeader(HttpRequestMessage httpRequest)
    {
        var headerValue = _bearerTokenHeaderValue;

        if (headerValue is null)
            return;

        httpRequest.Headers.TryAddWithoutValidation(HeaderNames.Authorization, 
                                                    headerValue);
    }

    /// <summary>
    /// Sign in to a Hearty server with credentials supplied through
    /// HTTP Basic authentication.
    /// </summary>
    /// <remarks>
    /// This method of authentication is not preferred in so far as
    /// the credentials will become visible to the Hearty server. 
    /// It is recommended instead that you initially authenticate 
    /// with OAuth interactively through your Web browser, and 
    /// retrieve a bearer token that you can pass in to all
    /// subsequent requests.
    /// </remarks>
    /// <returns>
    /// Asynchronous task that would return without any result
    /// if this client has successfully signed in.  The bearer
    /// token will be saved into <see cref="BearerToken" />.
    /// </returns>
    public async Task SignInAsync(string user, string password)
    {
        if (user is null)
            throw new ArgumentNullException(nameof(user));

        if (password is null)
            throw new ArgumentNullException(nameof(password));

        var url = CreateRequestUrl("auth/token");

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
                                            "Basic", EncodeBasicAuthentication(user, password));
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, "application/jwt");
        var response = await SendHttpMessageAsync(request, true, CancellationToken.None)
                                        .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        BearerToken = await response.Content.ReadAsStringAsync()
                                            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opaque data in text format for "Bearer Token" authentication
    /// to the Hearty server.
    /// </summary>
    public string? BearerToken
    {
        get
        {
            var headerValue = _bearerTokenHeaderValue;
            if (headerValue is null)
                return null;

            return headerValue[BearerAuthorizationPrefix.Length..];
        }
        set
        {
            _bearerTokenHeaderValue = BearerAuthorizationPrefix + value;
        }
    }

    /// <summary>
    /// The bearer token set in <see cref="BearerToken" />,
    /// prefixed by <see cref="BearerAuthorizationPrefix" />.
    /// </summary>
    private string? _bearerTokenHeaderValue;

    /// <summary>
    /// The scheme as a string, followed by a space, 
    /// used in the value of the HTTP Authorization to signify
    /// Bearer Authentication.
    /// </summary>
    private const string BearerAuthorizationPrefix = "Bearer ";

    #endregion
}
