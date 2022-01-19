using JobBank.WebSockets;
using MessagePack;
using Microsoft.Extensions.Logging;
using System;
using System.Net.WebSockets;
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

        public async ValueTask<WebSocket> CreateClientSocketAsync()
        {
            var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri("ws://localhost:5000/ws"), default)
                           .ConfigureAwait(false);
            return webSocket;
        }

        public async Task ExerciseClientSocketAsync()
        {
            var webSocket = await CreateClientSocketAsync();
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
