using System;
using System.IO;
using System.Net.Http;
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

        /// <summary>
        /// Wrap a strongly-typed interface for Job Bank over 
        /// a standard HTTP client.
        /// </summary>
        public JobBankClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <inheritdoc cref="IDisposable.Dispose" />
        public void Dispose() => _httpClient.Dispose();

        /// <summary>
        /// Post a job for the server to queue up and run.
        /// </summary>
        public async Task<PromiseId> PostJobAsync(string routeKey, HttpContent content)
        {
            var response = await _httpClient.PostAsync("jobs/v1/queue/" + routeKey, content)
                                            .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

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
    }
}