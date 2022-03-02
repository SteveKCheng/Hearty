using Hearty.Common;
using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.FormattableString;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Hearty.Client
{
    /// <summary>
    /// A strongly-typed interface for clients 
    /// to interact with the Hearty server via its HTTP ReST protocol.
    /// </summary>
    public class HeartyClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private bool _leaveOpen;

        /// <summary>
        /// Wrap a strongly-typed interface for Hearty over 
        /// a standard HTTP client.
        /// </summary>
        public HeartyClient(HttpClient httpClient, bool leaveOpen = false)
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

        /// <summary>
        /// Post a job for the Hearty server to queue up and launch.
        /// </summary>
        /// <param name="routeKey">
        /// The route on the Hearty server to post the job to.
        /// The choices and meanings for this string depends on the
        /// application-level customization of the Hearty server.
        /// </param>
        /// <param name="content">
        /// The input data for the job.  The interpretation of the
        /// data depends on <paramref name="routeKey" /> and the
        /// application-level customization of the Hearty server.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the operation of posting
        /// the job. Note, however, if cancellation races with
        /// successful posting of the job, the job is not cancelled.
        /// </param>
        /// <returns>
        /// ID of the remote promise which is used by the server
        /// to uniquely identify the job.
        /// </returns>
        public async Task<PromiseId> PostJobAsync(string routeKey, 
                                                  HttpContent content,
                                                  CancellationToken cancellationToken = default)
        {
            var response = await _httpClient.PostAsync("jobs/v1/queue/" + routeKey, 
                                                       content, 
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
                                        string? queue = null,
                                        int priority = -1)
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

            if (queue is not null)
            {
                AppendQuerySeparator(ref builder, ref paramCount);
                builder.Append("queue=");
                builder.Append(Uri.EscapeDataString(queue));
            }

            if (priority >= 0)
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

        /// <summary>
        /// Queue up a job for the Hearty server, and return results
        /// when it completes.
        /// </summary>
        /// <param name="route">
        /// The route on the Hearty server to post the job to.
        /// The choices and meanings for this string depends on the
        /// application-level customization of the Hearty server.
        /// </param>
        /// <param name="contentType">
        /// The desired IANA media types of the job results.
        /// </param>
        /// <param name="reader">
        /// De-serializes the response stream into the desired
        /// object of type <typeparamref name="T" />.
        /// </param>
        /// <param name="input">
        /// The input data for the job.  The interpretation of the
        /// data depends on <paramref name="route" /> and the
        /// application-level customization of the Hearty server.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the operation of posting
        /// the job. Note, however, if cancellation races with
        /// successful posting of the job, the job is not cancelled.
        /// </param>
        /// <returns>
        /// The de-serialized result from the job if it completes
        /// successfully.
        /// </returns>
        /// <typeparam name="T">
        /// The type of object the result should be expressed as.
        /// </typeparam>
        public async Task<T> RunJobAsync<T>(string route, 
                                            string contentType,
                                            HttpContent input,
                                            PayloadReader<T> reader,
                                            CancellationToken cancellationToken = default)
        {
            var url = CreateRequestUrl("jobs/v1/queue",
                                       route: route,
                                       wantResult: true);
            var message = new HttpRequestMessage(HttpMethod.Post, url);
            message.Headers.Accept.ParseAdd(contentType);
            message.Headers.Accept.ParseAdd(ExceptionPayload.JsonMediaType);
            message.Content = input;

            var response = await _httpClient.SendAsync(message,
                                                       HttpCompletionOption.ResponseHeadersRead,
                                                       cancellationToken)
                                            .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = response.Content;

            var actualContentType = content.Headers.TryGetSingleValue(HeaderNames.ContentType)
                ?? throw new InvalidDataException("The Content-Type returned in the response is unexpected. ");

            using var stream = await content.ReadAsStreamAsync(cancellationToken)
                                            .ConfigureAwait(false);

            T result = await reader.Invoke(new ParsedContentType(actualContentType),
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

        /// <summary>
        /// Wait for and obtain the result contained by a remote promise.
        /// </summary>
        /// <param name="promiseId">The ID of the desired promise on the
        /// server.
        /// </param>
        /// <param name="contentType">The desired content type of result
        /// to receive. </param>
        /// <param name="timeout">
        /// Directs this method to stop waiting if the 
        /// the server does not make the result available by this
        /// time interval.
        /// </param>
        /// <param name="cancellation">
        /// Can be triggered to cancel the request.
        /// </param>
        /// <returns>
        /// Forward-only read-only stream providing the bytes of 
        /// the desired result.
        /// </returns>
        public async Task<Stream> GetContentAsync(PromiseId promiseId, 
                                                  string contentType,
                                                  TimeSpan timeout,
                                                  CancellationToken cancellation = default)
        {
            var url = CreateRequestUrl("jobs/v1/id/", promiseId,
                                       timeout: (timeout != TimeSpan.Zero) ? timeout : null);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(contentType);
            request.Headers.Accept.ParseAdd(ExceptionPayload.JsonMediaType);

            var response = await _httpClient.SendAsync(request, cancellation)
                                            .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var content = response.Content;
            if (!VerifyContentType(content, contentType))
                throw new InvalidDataException("The Content-Type returned in the response is unexpected. ");

            return await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        }

        private const string MultipartParallelMediaType = "multipart/parallel";

        private static void VerifyContentTypeSyntax(string contentType)
        {
            if (!new ParsedContentType(contentType).IsValid)
                throw new FormatException("The content type to accept for the job output is invalid. ");
        }

        /// <summary>
        /// Download a stream of items from a promise/job stored
        /// on the Hearty server.
        /// </summary>
        /// <typeparam name="T">
        /// The type of object the payload will be de-serialized to.
        /// </typeparam>
        /// <param name="promiseId">
        /// The ID of the promise or job on the Hearty server
        /// whose content is a stream of items.
        /// </param>
        /// <param name="contentType"></param>
        /// <param name="reader">
        /// De-serializes the payload of each item into the desired
        /// object of type <typeparamref name="T" />.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the entire downloading
        /// operation.
        /// </param>
        /// <returns>
        /// Asynchronous task returning the stream of items, once
        /// the streaming connection to the server for the desired
        /// promise has been established.  The stream itself is 
        /// asynchronous, as it will be incrementally downloading
        /// items.  The server may be also be producing
        /// the items concurrently, so that it also cannot make
        /// them available immediately.  The stream may be enumerated
        /// only once as it is not buffered.
        /// </returns>
        public async Task<IAsyncEnumerable<KeyValuePair<int, T>>> 
            GetItemStreamAsync<T>(PromiseId promiseId,
                                  string contentType,
                                  PayloadReader<T> reader,
                                  CancellationToken cancellationToken = default)
        {
            VerifyContentTypeSyntax(contentType);

            var url = CreateRequestUrl("jobs/v1/id/", promiseId);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(MultipartParallelMediaType);
            request.Headers.Accept.ParseAdd(ExceptionPayload.JsonMediaType);
            request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, contentType);
            request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, ExceptionPayload.JsonMediaType);
                
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
            return MakeItemsAsyncEnumerable(multipartReader, reader, cancellationToken);
        }

        /// <summary>
        /// Queue up a job for the Hearty server, and return a
        /// stream of results.
        /// </summary>
        /// <param name="route">
        /// The route on the Hearty server to post the job to.
        /// The choices and meanings for this string depends on the
        /// application-level customization of the Hearty server.
        /// </param>
        /// <param name="contentType">
        /// The desired IANA media types of the job results.
        /// </param>
        /// <param name="reader">
        /// De-serializes the response stream into the desired
        /// object of type <typeparamref name="T" />.
        /// </param>
        /// <param name="input">
        /// The input data for the job.  The interpretation of the
        /// data depends on <paramref name="route" /> and the
        /// application-level customization of the Hearty server.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the operation of posting
        /// the job. Note, however, if cancellation races with
        /// successful posting of the job, the job is not cancelled.
        /// </param>
        /// <returns>
        /// Asynchronous task returning the stream of items, once
        /// the streaming connection to the server for the desired
        /// promise has been established.  The stream itself is 
        /// asynchronous, as it will be incrementally downloading
        /// items.  The server may be also be producing
        /// the items concurrently, so that it also cannot make
        /// them available immediately.  The stream may be enumerated
        /// only once as it is not buffered.
        /// </returns>
        /// <typeparam name="T">
        /// The type of object the result should be expressed as.
        /// </typeparam>
        public async Task<IAsyncEnumerable<KeyValuePair<int, T>>>
            RunJobStreamAsync<T>(string route,
                                 string contentType,
                                 HttpContent input,
                                 PayloadReader<T> reader,
                                 CancellationToken cancellationToken = default)
        {
            VerifyContentTypeSyntax(contentType);

            var url = CreateRequestUrl("jobs/v1/queue",
                                       route: route,
                                       wantResult: true);

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = input;
            request.Headers.Accept.ParseAdd(MultipartParallelMediaType);
            request.Headers.Accept.ParseAdd(ExceptionPayload.JsonMediaType);
            request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, contentType);
            request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, ExceptionPayload.JsonMediaType);

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
            return MakeItemsAsyncEnumerable(multipartReader, reader, cancellationToken);
        }

        private static async IAsyncEnumerable<KeyValuePair<int, T>> MakeItemsAsyncEnumerable<T>(
            MultipartReader reader, 
            PayloadReader<T> processor,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)
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
                    var payload = await ExceptionPayload.TryReadAsync(contentType, section.Body, cancellationToken)
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

                T item = await processor.Invoke(contentType, section.Body, cancellationToken)
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

        /// <summary>
        /// Request a job on the Hearty server 
        /// be cancelled on behalf of this client.
        /// </summary>
        /// <remarks>
        /// If the job is currently being shared by other clients,
        /// it is not interrupted unless all other clients 
        /// also relinquish their interest in their job, 
        /// by cancellation.
        /// </remarks>
        /// <param name="promiseId">The ID of the job to cancel. </param>
        /// <param name="queue">The queue that the job has been
        /// pushed into, for the current client.  This argument
        /// is used to identify a specific instance of the job
        /// if the client has pushed it onto multiple queues.
        /// </param>
        /// <param name="priority">
        /// The priority of that existing job.  This argument
        /// is used to identify a specific instance of the job
        /// if the client has pushed it for multiple priorities.
        /// </param>
        /// <returns>
        /// Asynchronous task that completes when the server
        /// acknowledges the request to cancel the job.
        /// </returns>
        public async Task CancelJobAsync(PromiseId promiseId, 
                                         string? queue = null, 
                                         int? priority = null)
        {
            var url = CreateRequestUrl("jobs/v1/id/",
                                       promiseId,
                                       queue: queue,
                                       priority: priority ?? -1);

            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            AddAuthorizationHeader(request);

            var response = await _httpClient.SendAsync(request)
                                            .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Stop a job on the Hearty server for all clients, 
        /// causing it to return a "cancelled" result.
        /// </summary>
        /// <remarks>
        /// This operation typically requires administrator-level
        /// authorization on the Hearty server.  As stopping a job
        /// is implemented cooperatively, even after this method
        /// returns asynchronously, the job may not have actually
        /// stopped yet.
        /// </remarks>
        /// <param name="promiseId">The ID of the job to kill. </param>
        /// <returns>
        /// Asynchronous task that completes when the server
        /// acknowledges the request to stop the job.
        /// </returns>
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
            var headerValue = _bearerTokenHeaderValue ??
                    throw new InvalidOperationException(
                        "This client must sign in to the Hearty server " +
                        "first or be supplied a bearer token. ");
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
            request.Headers.Accept.ParseAdd("application/jwt");
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
}