using Hearty.Common;
using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Client
{
    /// <summary>
    /// A strongly-typed interface for clients 
    /// to interact with the Hearty server.
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
        /// Post a job for the server to queue up and run.
        /// </summary>
        /// <returns>
        /// ID of the remote promise which is used by the server
        /// to uniquely identify the job request.
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

            string? promiseIdStr;
            if (!response.Headers.TryGetValues("x-promise-id", out var values) ||
                (promiseIdStr = values.SingleOrDefaultNoException()) is null)
            {
                throw new InvalidDataException(
                    "The server did not report a promise ID for the job positing as expected. ");
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
            var url = $"jobs/v1/id/{promiseId}";
            if (timeout != TimeSpan.Zero)
                url += $"?timeout={RestApiUtilities.FormatTimeSpan(timeout)}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(contentType);

            var response = await _httpClient.SendAsync(request)
                                            .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var content = response.Content;
            if (!VerifyContentType(content.Headers.ContentType, contentType))
                throw new InvalidDataException("The Content-Type returned in the response is unexpected. ");

            return await content.ReadAsStreamAsync().ConfigureAwait(false);
        }

        public async Task<IAsyncEnumerable<KeyValuePair<int, T>>> 
            GetItemStreamAsync<T>(PromiseId promiseId,
                                  string contentType,
                                  PayloadProcessor<T> processor,
                                  CancellationToken cancellationToken = default)
        {
            var url = $"jobs/v1/id/{promiseId}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("multipart/parallel"));

            var response = await _httpClient.SendAsync(request, 
                                                       HttpCompletionOption.ResponseHeadersRead, 
                                                       cancellationToken)
                                            .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            var content = response.Content;
            var responseContentType = content.Headers.ContentType;

            if (!VerifyContentType(responseContentType, "multipart/parallel"))
                throw new InvalidDataException("The Content-Type returned in the response is unexpected. ");

            var boundary = responseContentType!.Parameters.SingleOrDefault(
                            param => string.Equals(param.Name, "boundary", StringComparison.OrdinalIgnoreCase))
                            ?.Value ?? throw new InvalidDataException("Multi-part message is missing its boundary parameter. ");

            var multipartReader = new MultipartReader(boundary, 
                                                      content.ReadAsStream(cancellationToken));
            return MakeItemsAsyncEnumerable(multipartReader, processor, cancellationToken);
        }

        private static async IAsyncEnumerable<KeyValuePair<int, T>> MakeItemsAsyncEnumerable<T>(
            MultipartReader reader, 
            PayloadProcessor<T> processor,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)
                                          .ConfigureAwait(false)) is not null)
            {
                var ordinalString = section.Headers![HeartyHttpHeaders.Ordinal];
                if (ordinalString.Count != 1)
                    throw new InvalidDataException("The 'Ordinal' header is expected in an item in the multi-part message but is not found. ");

                // Is an exception
                if (string.Equals(ordinalString[0], "Trailer"))
                {
                    // Assume application/vnd.hearty.exception+json
                    var payload = await JsonSerializer.DeserializeAsync<ExceptionPayload>(section.Body, options: null, cancellationToken)
                                                      .ConfigureAwait(false);
                    throw payload!.ToException();
                }

                if (!int.TryParse(ordinalString[0], out int ordinal))
                    throw new InvalidDataException("The 'Ordinal' header is in an item in the multi-part message is invalid. ");

                T item = await processor.Invoke(section.ContentType, section.Body, cancellationToken)
                                        .ConfigureAwait(false);
                yield return KeyValuePair.Create(ordinal, item);
            }
        }

        private static bool VerifyContentType(MediaTypeHeaderValue? actual, string expected)
        {
            if (actual != null)
            {
                if (!string.Equals(actual.MediaType,
                                   expected,
                                   StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

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

        public async Task SignInAsync()
        {
            var url = "auth/token";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", EncodeBasicAuthentication("admin", "admin"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/jwt"));
            var response = await _httpClient.SendAsync(request)
                                            .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            BearerToken = await response.Content.ReadAsStringAsync()
                                                .ConfigureAwait(false);
        }

        public string? BearerToken { get; set; }
    }

    public delegate ValueTask<T> PayloadProcessor<T>(string? contentType,
                                                     Stream stream,
                                                     CancellationToken cancellationToken);
}