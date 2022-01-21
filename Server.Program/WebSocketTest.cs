using JobBank.WebSockets;
using JobBank.Work;
using MessagePack;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server.Program
{
    public class WebSocketTest
    {
        internal readonly RpcRegistry _registry = new RpcRegistry();

        private readonly ILogger _logger;

        public WebSocketTest(ILogger<WebSocketTest> logger)
        {
            _logger = logger;
            _registry.Add<WsEchoRequest, WsEchoReply>(0x8, this.DoEchoReplyAsync);
        }

        private async ValueTask<WsEchoReply> DoEchoReplyAsync(WsEchoRequest request, 
                                                              RpcConnection connection,
                                                              CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received ECHO request message {text}, waiting 200 ms", request.Text);
            await Task.Delay(200).ConfigureAwait(false);
            _logger.LogInformation("About to send ECHO reply message");
            return new WsEchoReply { Echo = request.Text };
        }

        public async Task ExerciseClientSocketAsync()
        {
            var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri("ws://localhost:5000/ws"), default)
                           .ConfigureAwait(false);
            var rpc = new WebSocketRpc(webSocket, _registry);

            for (int i = 0; i < 10; ++i)
            {
                var request = new WsEchoRequest { Text = $"testing {i}" };
                _logger.LogInformation("Sending ECHO request message {text}", request.Text);
                var reply = await rpc.InvokeRemotelyAsync<WsEchoRequest, WsEchoReply>(0x8, request, default);
                _logger.LogInformation("Got ECHO reply message {text}", reply.Echo);

                await Task.Delay(1000);
            }

            rpc.Quit();
            await rpc.WaitForCloseAsync();
            webSocket.Dispose();
        }

        public async Task CreateFakeRemoteWorkerHostsAsync()
        {
            for (int i = 0; i < 10; ++i)
            {
                var settings = new RegisterWorkerRequestMessage
                {
                    Name = $"fake-worker-{i}",
                    Concurrency = 10
                };

                _logger.LogInformation("Attempting to start fake worker #{worker}", i);

                await WorkerHost.ConnectAndStartAsync(
                    new JobSubmissionImpl(_logger, i),
                    settings,
                    "ws://localhost:5000/ws/worker",
                    null,
                    CancellationToken.None); ;

                _logger.LogInformation("Successfully started fake worker #{worker}", i);
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

    [MessagePackObject]
    public struct WsEchoRequest
    {
        [Key("text")]
        public string Text { get; set; }
    }

    [MessagePackObject]
    public struct WsEchoReply
    {
        [Key("echo")]
        public string Echo { get; set; }
    }
}
