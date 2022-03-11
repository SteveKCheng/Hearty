using Hearty.Common;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Hearty.Client;

/// <summary>
/// Strongly-typed access to interact with a
/// a Hearty server over HTTP.
/// </summary>
public class HeartyHttpClient : IHeartyClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _leaveOpen;

    /// <summary>
    /// Wrap a strongly-typed interface for Hearty over 
    /// a standard HTTP client.
    /// </summary>
    public HeartyHttpClient(HttpClient httpClient, bool leaveOpen = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _leaveOpen = leaveOpen;
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
        var url = CreateRequestUrl("jobs/v1/queue/",
                                   route: route,
                                   queue: queue);

        var response = await _httpClient.PostAsync(url, 
                                                   input.CreateHttpContent(), 
                                                   cancellationToken)
                                        .ConfigureAwait(false);
        EnsureSuccessStatusCodeEx(response);

        return GetPromiseId(response.Headers);
    }

#if NET6_0_OR_GREATER
    private readonly struct TimeSpanFormatWrapper : ISpanFormattable
    {
        public readonly TimeSpan TimeSpan;

        public TimeSpanFormatWrapper(TimeSpan timeSpan) => TimeSpan = timeSpan;

        public string ToString(string? format, IFormatProvider? formatProvider)
            => RestApiUtilities.FormatExpiry(TimeSpan);

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => RestApiUtilities.TryFormatExpiry(TimeSpan, destination, out charsWritten);
    }
#endif

    private string CreateRequestUrl(string path,
                                    PromiseId? promiseId = null,
                                    string? route = null,
                                    TimeSpan? timeout = null,
                                    bool? wantResult = null,
                                    JobQueueKey queue = default)
    {
        var builder = new ValueStringBuilder(stackalloc char[1024]);

        builder.Append(path);

        if (route is not null)
            builder.Append(route);

        if (promiseId != null)
        {
#if NET6_0_OR_GREATER
            builder.Append(promiseId.Value);
#else
            builder.Append(promiseId.ToString());
#endif
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
#if NET6_0_OR_GREATER
            builder.Append(new TimeSpanFormatWrapper(timeout.Value));
#else
            builder.Append(RestApiUtilities.FormatExpiry(timeout.Value));
#endif
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
#if NET6_0_OR_GREATER
            builder.Append(priority);
#else
            builder.Append(priority.ToString(provider: null));
#endif
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
        var url = CreateRequestUrl("jobs/v1/queue",
                                   route: route,
                                   wantResult: true,
                                   queue: queue);
        var message = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuthorizationHeader(message);
        reader.AddAcceptHeaders(message.Headers);
        message.Headers.TryAddWithoutValidation(HeaderNames.Accept, ExceptionPayload.JsonMediaType);
        message.Content = input.CreateHttpContent();

        var response = await _httpClient.SendAsync(message,
                                                   HttpCompletionOption.ResponseHeadersRead,
                                                   cancellationToken)
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
        var url = CreateRequestUrl("jobs/v1/id/", promiseId,
                                   timeout: (timeout != TimeSpan.Zero) ? timeout : null);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request);
        reader.AddAcceptHeaders(request.Headers);

        var response = await _httpClient.SendAsync(request, 
                                                   HttpCompletionOption.ResponseHeadersRead, 
                                                   cancellationToken)
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

    private async IAsyncEnumerable<KeyValuePair<int, T>> 
        GetResultStreamInternalAsync<T>(
            PromiseId promiseId,
            HttpRequestMessage request,
            PayloadReader<T> reader,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(request,
                                                   HttpCompletionOption.ResponseHeadersRead,
                                                   cancellationToken)
                                        .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var content = response.Content;

        var boundary = RestApiUtilities.GetMultipartBoundary(
                        content.Headers.TryGetSingleValue(HeaderNames.ContentType));

        var multipartReader = new MultipartReader(boundary,
                                                  content.ReadAsStream(cancellationToken));

        var enumerator = MakeItemsAsyncEnumerator(promiseId,
                                                  multipartReader,
                                                  reader,
                                                  cancellationToken);

        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            yield return enumerator.Current;
    }


    /// <inheritdoc cref="IHeartyClient.GetResultStreamAsync" />
    public IAsyncEnumerable<KeyValuePair<int, T>> 
        GetResultStreamAsync<T>(
            PromiseId promiseId,
            PayloadReader<T> reader,
            CancellationToken cancellationToken = default)
    {
        var url = CreateRequestUrl("jobs/v1/id/", promiseId);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request);
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, MultipartParallelMediaType);
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, ExceptionPayload.JsonMediaType);
        request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, ExceptionPayload.JsonMediaType);
        reader.AddAcceptHeaders(request.Headers, HeartyHttpHeaders.AcceptItem);

        return GetResultStreamInternalAsync(
                promiseId, 
                request, 
                reader, 
                cancellationToken);
    }

    private async IAsyncEnumerable<KeyValuePair<int, T>>
        RunJobStreamInternalAsync<T>(
            HttpRequestMessage request,
            PayloadReader<T> reader,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(request,
                                                   HttpCompletionOption.ResponseHeadersRead,
                                                   cancellationToken)
                                        .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var content = response.Content;

        var promiseId = GetPromiseId(content.Headers);

        var boundary = RestApiUtilities.GetMultipartBoundary(
                        content.Headers.TryGetSingleValue(HeaderNames.ContentType));

        var multipartReader = new MultipartReader(boundary,
                                                  content.ReadAsStream(cancellationToken));

        var enumerator = MakeItemsAsyncEnumerator(
                            promiseId,
                            multipartReader,
                            reader,
                            cancellationToken);

        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            yield return enumerator.Current;
    }


    /// <inheritdoc cref="IHeartyClient.RunJobStreamAsync" />
    public IAsyncEnumerable<KeyValuePair<int, T>>
        RunJobStreamAsync<T>(string route,
                             PayloadWriter input,
                             PayloadReader<T> reader,
                             JobQueueKey queue = default,
                             CancellationToken cancellationToken = default)
    {
        var url = CreateRequestUrl("jobs/v1/queue",
                                   route: route,
                                   wantResult: true,
                                   queue: queue);

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuthorizationHeader(request);
        request.Content = input.CreateHttpContent();
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, MultipartParallelMediaType);
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, ExceptionPayload.JsonMediaType);
        request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, ExceptionPayload.JsonMediaType);
        reader.AddAcceptHeaders(request.Headers, HeartyHttpHeaders.AcceptItem);

        return RunJobStreamInternalAsync(request, reader, cancellationToken);
    }

    private static async IAsyncEnumerator<KeyValuePair<int, T>>
        MakeItemsAsyncEnumerator<T>(
            PromiseId promiseId,
            MultipartReader multipartReader,
            PayloadReader<T> payloadReader,
            CancellationToken cancellationToken = default)
    {
        MultipartSection? section;
        while ((section = await multipartReader.ReadNextSectionAsync(cancellationToken)
                                               .ConfigureAwait(false)) is not null)
        {
            var ordinalString = section.Headers![HeartyHttpHeaders.Ordinal];
            if (ordinalString.Count != 1)
                throw new InvalidDataException("The 'Ordinal' header is expected in an item in the multi-part message but is not found. ");

            var contentType = new ParsedContentType(
                                section.ContentType
                                ?? throw new InvalidDataException(
                                    "The 'Content-Type' header is missing for an item " +
                                    "in the multi-part message. "));

            // Is an exception
            if (string.Equals(ordinalString[0], "Trailer"))
            {
                var payload = await ExceptionPayload.TryReadAsync(promiseId,
                                                                  contentType,
                                                                  section.Body,
                                                                  cancellationToken)
                                                    .ConfigureAwait(false);
                if (payload is null)
                {
                    throw new InvalidDataException(
                        "The format of a trailer object from the Hearty server " +
                        "is expected to be ExceptionPayload, but is not. ");
                }

                throw payload.ToException();
            }

            if (!int.TryParse(ordinalString[0], out int ordinal))
                throw new InvalidDataException("The 'Ordinal' header is in an item in the multi-part message is invalid. ");

            var promiseIdString = section.Headers![HeartyHttpHeaders.PromiseId];
            if (promiseIdString.Count != 1 ||
                !PromiseId.TryParse(promiseIdString[0], out var itemPromiseId))
            {
                throw new InvalidDataException("The server did not report the Promise ID of the item in a multi-part message properly. ");
            }

            T item = await payloadReader.ReadFromStreamAsync(itemPromiseId,
                                                             contentType,
                                                             section.Body,
                                                             cancellationToken)
                                        .ConfigureAwait(false);

            yield return KeyValuePair.Create(ordinal, item);
        }
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
        var url = CreateRequestUrl("jobs/v1/id/",
                                   promiseId,
                                   queue: queue);

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        AddAuthorizationHeader(request);

        var response = await _httpClient.SendAsync(request)
                                        .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc cref="IHeartyClient.KillJobAsync" />
    public async Task KillJobAsync(PromiseId promiseId)
    {
        var url = CreateRequestUrl("jobs/v1/admin/id/", promiseId);

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        AddAuthorizationHeader(request);

        var response = await _httpClient.SendAsync(request)
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
        var response = await _httpClient.SendAsync(request)
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
