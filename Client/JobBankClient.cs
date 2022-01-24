using JobBank.Common;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Client
{
    /// <summary>
    /// A strongly-typed interface for clients 
    /// to interact with the Job Bank server.
    /// </summary>
    public class JobBankClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private bool _leaveOpen;

        /// <summary>
        /// Wrap a strongly-typed interface for Job Bank over 
        /// a standard HTTP client.
        /// </summary>
        public JobBankClient(HttpClient httpClient, bool leaveOpen = false)
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
    }
}