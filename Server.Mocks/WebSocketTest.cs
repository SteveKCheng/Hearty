using JobBank.Work;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server.Mocks
{
    public class WebSocketTest
    {
        private readonly ILogger _logger;

        public WebSocketTest(ILogger<WebSocketTest> logger)
        {
            _logger = logger;
        }

        public async Task CreateFakeRemoteWorkerHostsAsync(string host, int? port, bool secure)
        {
            var uri = new UriBuilder(scheme: secure ? "wss" : "ws",
                                     host: host,
                                     port: port ?? -1,
                                     pathValue: "ws/worker").Uri;

            _logger.LogInformation("Fake workers will connect by WebSockets on URL: {uri}", uri);

            for (int i = 0; i < 10; ++i)
            {
                var settings = new RegisterWorkerRequestMessage
                {
                    Name = $"fake-worker-{i}",
                    Concurrency = 10
                };

                _logger.LogInformation("Attempting to start fake worker #{worker}", i);

                try
                {
                    await WorkerHost.ConnectAndStartAsync(
                        new JobSubmissionImpl(_logger, i),
                        settings,
                        uri,
                        null,
                        CancellationToken.None);
                    _logger.LogInformation("Successfully started fake worker #{worker}", i);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to start fake worker #{worker}", i);
                }
            }
        }

        private sealed class JobSubmissionImpl : IJobSubmission
        {
            private ILogger _logger;
            private int _workerId;

            public JobSubmissionImpl(ILogger logger, int id)
            {
                _logger = logger;
                _workerId = id;
            }

            public async ValueTask<RunJobReplyMessage> RunJobAsync(RunJobRequestMessage request, CancellationToken cancellationToken)
            {
                _logger.LogInformation("Starting job for execution ID {executionId} on fake remote worker #{worker}", 
                                       request.ExecutionId, _workerId);

                // Mock work
                await Task.Delay(request.InitialWait, cancellationToken)
                          .ConfigureAwait(false);

                var reply = new RunJobReplyMessage
                {
                    ContentType = "application/json",
                    Data = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(@"{ ""status"": ""finished remote job"" }"))
                };

                _logger.LogInformation("Completing job for execution ID {executionId} on fake remote worker #{worker}", 
                                       request.ExecutionId, _workerId);

                return reply;
            }
        }
    }
}
