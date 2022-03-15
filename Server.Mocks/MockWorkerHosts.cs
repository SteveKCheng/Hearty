using Hearty.Work;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.Mocks
{
    /// <summary>
    /// Generates worker hosts running locally that connect 
    /// to a WebSocket endpoint that accepts them, for testing.
    /// </summary>
    public class MockWorkerHosts
    {
        private readonly ILogger _logger;

        /// <summary>
        /// Prepare to generate worker hosts.
        /// </summary>
        /// <param name="logger">
        /// Logs whenever worker hosts are started.
        /// </param>
        public MockWorkerHosts(ILogger<MockWorkerHosts> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Create fake worker hosts connecting to a WebSocket endpoint
        /// for job distribution from a Hearty server.
        /// </summary>
        /// <param name="host">The host name or IP address of the WebSocket
        /// endpoint. </param>
        /// <param name="port">The port that the WebSocket endpoint is running on.
        /// </param>
        /// <param name="secure">
        /// If true, the WebSocket connection will be encrypted
        /// with TLS (Transport Layer Security).
        /// If false, the WebSocket connection is naked.
        /// </param>
        /// <param name="path">
        /// The path specified to connect to the WebSocket endpoint.
        /// Typically it is "/ws/worker". If null, that will be the 
        /// default.  The caller may need to override it to have
        /// a base path prepended.
        /// </param>
        /// <param name="count">
        /// The number of worker hosts to instantiate.
        /// </param>
        /// <param name="concurrency">
        /// The degree of concurrency allowed for each worker host.
        /// </param>
        /// <param name="namePrefix">
        /// The prefix on the names assigned to each worker host.
        /// This string will be suffixed with the ordinal of the worker,
        /// numbers from 1 to <paramref name="count" />.
        /// </param>
        /// <param name="factory">
        /// Supplies the implementation object for each worker host.
        /// The first argument is the name of the host; the second
        /// argument is <paramref name="concurrency" />.
        /// </param>
        public async Task CreateFakeWorkersAsync(string host, 
                                                 int? port, 
                                                 bool secure,
                                                 string? path,
                                                 int count,
                                                 int concurrency,
                                                 string namePrefix,
                                                 Func<string, int, IJobSubmission> factory)
        {
            var uri = new UriBuilder(scheme: secure ? "wss" : "ws",
                                     host: host,
                                     port: port ?? -1,
                                     pathValue: path ?? WorkerHost.WebSocketsDefaultPath).Uri;

            _logger.LogInformation("Fake workers will connect by WebSockets on URL: {uri}", uri);

            for (int i = 0; i < count; ++i)
            {
                var name = $"{namePrefix}{i + 1}";
                var settings = new RegisterWorkerRequestMessage
                {
                    Name = name,
                    Concurrency = (ushort)concurrency
                };

                _logger.LogInformation("Attempting to start fake worker #{workerName}", name);

                try
                {
                    await WorkerHost.ConnectAndStartAsync(
                        factory.Invoke(name, concurrency),
                        settings,
                        uri,
                        null,
                        CancellationToken.None);
                    _logger.LogInformation("Successfully started fake worker #{workerName}", name);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to start fake worker #{workerName}", name);
                }
            }
        }
    }
}
