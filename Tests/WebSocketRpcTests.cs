using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.WebSockets;
using JobBank.WebSockets;
using System.IO.Pipes;
using System.Security.Cryptography;
using MessagePack;
using System.Threading;
using Xunit;

namespace JobBank.Tests
{
    public class WebSocketRpcTests
    {
        private static uint GetRandomInteger()
        {
            using var p = new RNGCryptoServiceProvider();
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            p.GetBytes(buffer);
            return BitConverter.ToUInt32(buffer);
        }

        private (WebSocketRpc Client, WebSocketRpc Server) CreateConnections()
        {
            var pipeName = $"WebSocketRpcTests-{GetRandomInteger():8X}";
            var pipeOptions = PipeOptions.Asynchronous |
                              PipeOptions.CurrentUserOnly |
                              PipeOptions.WriteThrough;

            // Create pair of local pipes which talk to each other bi-directionally.
            // Even though anonymous "socket pairs" are possible under Unix,
            // .NET's API only allows duplex mode when the pipes are "named",
            // so we have to generate a dummy name.
            var serverStream = new NamedPipeServerStream(
                                pipeName: pipeName,
                                direction: PipeDirection.InOut,
                                maxNumberOfServerInstances: 1,
                                transmissionMode: PipeTransmissionMode.Byte,
                                options: pipeOptions);
            var clientStream = new NamedPipeClientStream(
                                serverName: ".",
                                pipeName: pipeName,
                                direction: PipeDirection.InOut,
                                options: pipeOptions);

            clientStream.Connect();
            serverStream.WaitForConnection();

            Assert.True(serverStream.IsConnected);
            Assert.True(clientStream.IsConnected);

            var keepAliveInterval = TimeSpan.FromSeconds(60);

            // Layer on the WebSocket protocol on top of the bi-directional streams.
            var serverWebSocket = WebSocket.CreateFromStream(
                                    serverStream,
                                    isServer: true,
                                    subProtocol: null,
                                    keepAliveInterval: keepAliveInterval);
            var clientWebSocket = WebSocket.CreateFromStream(
                                    clientStream,
                                    isServer: false,
                                    subProtocol: null,
                                    keepAliveInterval: keepAliveInterval);

            var registry = new RpcRegistry();
            registry.Add(0x1, (RpcFunction<Func1Request, Func1Reply>)this.Func1Async);

            // Finally, apply the RPC protocol on top of the WebSockets.
            var serverRpc = new WebSocketRpc(serverWebSocket, registry, true);
            var clientRpc = new WebSocketRpc(clientWebSocket, registry, false);

            return (clientRpc, serverRpc);
        }

        private int _func1InvocationsFromServer;
        private int _func1InvocationsFromClient;

        private async ValueTask<Func1Reply> Func1Async(Func1Request request, 
                                                       RpcConnection connection, 
                                                       CancellationToken cancellationToken)
        {
            var isServer = (bool)connection.State!;
            if (isServer)
                _func1InvocationsFromClient++;
            else
                _func1InvocationsFromServer++;

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            return new Func1Reply
            {
                Answer = $"My answer to the question: {request.Question}"
            };
        }

        [MessagePackObject(keyAsPropertyName: true)]
        public struct Func1Request
        {
            public string Question { get; set; }
        }

        [MessagePackObject(keyAsPropertyName: true)]
        public struct Func1Reply
        {
            public string Answer { get; set; }
        }

        private static ValueTask<Func1Reply> InvokeFunc1Async(WebSocketRpc connection, 
                                                              Func1Request request,
                                                              CancellationToken cancellationToken)
            => connection.InvokeRemotelyAsync<Func1Request, Func1Reply>(0x1, 
                                                                        request, 
                                                                        cancellationToken);

        private static async Task TestFunc1SeriallyAsync(WebSocketRpc connection, int count, bool isServer)
        {
            for (int i = 0; i < count; ++i)
            {
                var question = $"question {i + 1} from {(isServer ? "server" : "client")}";
                var reply = await InvokeFunc1Async(connection, new Func1Request
                {
                    Question = question
                }, default);
                Assert.Contains(question, reply.Answer);
            }
        }

        private static async Task TestFunc1InParallelAsync(WebSocketRpc connection, int count, bool isServer)
        {
            var questions = new string[count];
            var tasks = new Task<Func1Reply>[count];

            for (int i = 0; i < count; ++i)
            {
                questions[i] = $"question {i + 1} from {(isServer ? "server" : "client")}";

                tasks[i] = InvokeFunc1Async(connection, new Func1Request
                {
                    Question = questions[i]
                }, default).AsTask();
            }

            var replies = await Task.WhenAll(tasks);
            for (int i = 0; i < count; ++i)
                Assert.Contains(questions[i], replies[i].Answer);
        }

        [Fact]
        public async Task InvokeCalls()
        {
            var (clientRpc, serverRpc) = CreateConnections();

            // Run requests for Func1 in serial individually
            // on server and client, but server and client submit
            // requests in parallel.
            const int clientRpcCount = 5;
            const int serverRpcCount = 8;
            var clientRpcTask = Task.Run(() => TestFunc1SeriallyAsync(clientRpc, clientRpcCount, false));
            var serverRpcTask = Task.Run(() => TestFunc1SeriallyAsync(serverRpc, serverRpcCount, true));
            await Task.WhenAll(clientRpcTask, serverRpcTask);

            Assert.Equal(clientRpcCount, _func1InvocationsFromClient);
            Assert.Equal(serverRpcCount, _func1InvocationsFromServer);

            // Run requests for Func1 completely in parallel.
            _func1InvocationsFromClient = 0;
            _func1InvocationsFromServer = 0;
            clientRpcTask = Task.Run(() => TestFunc1InParallelAsync(clientRpc, clientRpcCount, false));
            serverRpcTask = Task.Run(() => TestFunc1InParallelAsync(serverRpc, serverRpcCount, true));
            await Task.WhenAll(clientRpcTask, serverRpcTask);

            Assert.Equal(clientRpcCount, _func1InvocationsFromClient);
            Assert.Equal(serverRpcCount, _func1InvocationsFromServer);

            clientRpc.RequestCompletion();
            serverRpc.RequestCompletion();
            await clientRpc.Completion;
            await serverRpc.Completion;
        }
    }


}
