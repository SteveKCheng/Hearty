using System;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Hearty.Carp;
using System.IO.Pipes;
using MessagePack;
using System.Threading;
using Xunit;
using Hearty.Utilities;
using System.IO;
using Hearty.Work;
using Hearty.Common;

namespace Hearty.Tests;

public class WebSocketRpcTests
{
    private static (NamedPipeClientStream ClientStream, 
                    NamedPipeServerStream ServerStream) 
        CreateNamedPipeStreamPair()
    {
        // Guid.NewGuid should be sufficiently cryptographically random
        // to avoid clashes with other processes.
        var pipeName = $"WebSocketRpcTests-{Guid.NewGuid().ToString()[0..8]}";
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

        return (clientStream, serverStream);
    }

    private (WebSocketRpc Client, WebSocketRpc Server) 
        CreateRpcConnections(Stream clientStream, Stream serverStream)
    {
        /*
        var (clientStream, serverStream) = CreateNamedPipeStreamPair();

        Assert.True(serverStream.IsConnected);
        Assert.True(clientStream.IsConnected);
        */

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

        var registry = new RpcRegistry(new RpcExceptionSerializer(RpcRegistry.StandardSerializeOptions));
        registry.Add(0x1, (RpcFunction<Func1Request, Func1Reply>)this.Func1Async);
        registry.Add(0x2, (RpcFunction<Func1Request, Func1Reply>)this.Func2Async);
        registry.Add(0x3, (RpcFunction<Func1Request, Func1Reply>)this.Func3Async);

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

        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        return new Func1Reply
        {
            Answer = $"My answer to the question: {request.Question}"
        };
    }

    private async ValueTask<Func1Reply> Func2Async(Func1Request request, 
                                                   RpcConnection connection,
                                                   CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new Exception($"I am throwing an exception to your question: {request.Question}");
    }

    private AsyncBarrier _func3Barrier = new AsyncBarrier(2);
    private SemaphoreSlim _func3Result = new SemaphoreSlim(0);

    private async ValueTask<Func1Reply> Func3Async(Func1Request request, 
                                                   RpcConnection connection,
                                                   CancellationToken cancellationToken)
    {
        await _func3Barrier.SignalAndWaitAsync();

        try
        {
            // Wait up to one minute for this asynchronous function
            // invocation to be cancelled.  Unfortunately, this waiting
            // must be based on time and not on a semaphore, because the
            // cancellation message in the RPC protocol is "fire and forget".
            for (int i = 0; i < (60 * 1000 / 20); ++i)
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
        catch (OperationCanceledException e) when (e.CancellationToken == cancellationToken)
        {
            // Needed to let the caller verify that the remote implementation
            // did receive the cancellation too, not just its local side.
            _func3Result.Release();
            throw;
        }

        return new Func1Reply { Answer = "This function did not get cancelled! " };
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

    private static ValueTask<Func1Reply> InvokeFunc2Async(WebSocketRpc connection,
                                                          Func1Request request,
                                                          CancellationToken cancellationToken)
        => connection.InvokeRemotelyAsync<Func1Request, Func1Reply>(0x2,
                                                                    request,
                                                                    cancellationToken);

    private static ValueTask<Func1Reply> InvokeFunc3Async(WebSocketRpc connection,
                                                          Func1Request request,
                                                          CancellationToken cancellationToken)
        => connection.InvokeRemotelyAsync<Func1Request, Func1Reply>(0x3,
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
        var (clientStream, serverStream) = DuplexStream.CreatePair();
        var (clientRpc, serverRpc) = CreateRpcConnections(clientStream, serverStream);

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

        var exceptionQuestion = "do you throw exceptions? ";

        var caughtException = await Assert.ThrowsAsync<RemoteWorkException>(async () =>
        {
            await InvokeFunc2Async(clientRpc,
                                   new Func1Request { Question = exceptionQuestion },
                                   CancellationToken.None);
        });
        Assert.Contains(exceptionQuestion, caughtException.InnerException?.Message);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var cancelSource = new CancellationTokenSource();
                var func3Task = InvokeFunc3Async(
                                 clientRpc,
                                 new Func1Request { Question = exceptionQuestion },
                                 cancelSource.Token);

                // Ensure Func3Async is executing when we cancel
                await _func3Barrier.SignalAndWaitAsync();
                cancelSource.Cancel();

                await func3Task;
            });

        // The corresponding release of this semaphore should happen once
        // the remote implementation receives the cancellation trigger.
        // Note that, for reliability when running on real, fallible
        // remote hosts, the OperationCanceledException above is thrown
        // locally without waiting for the remote call to actually be
        // cancelled.  If the semaphore does not get released then
        // WaitAsync will timeout and thus fail this unit test.
        Assert.True(await _func3Result.WaitAsync(TimeSpan.FromSeconds(10)));

        clientRpc.Quit();
        Assert.True(clientRpc.IsClosingStarted);
        await clientRpc.WaitForCloseAsync();
        Assert.True(clientRpc.HasClosed);

        // serverRpc should automatically terminate when clientRpc terminated.
        Assert.True(serverRpc.IsClosingStarted);
        await serverRpc.WaitForCloseAsync();
        Assert.True(serverRpc.HasClosed);
    }

    [Fact]
    public async Task AbruptClose()
    {
        var (clientStream, serverStream) = DuplexStream.CreatePair();
        var (clientRpc, serverRpc) = CreateRpcConnections(clientStream, serverStream);

        var exceptionQuestion = "Testing abrupt closes of RPC connections";
        var invokeTask = InvokeFunc3Async(clientRpc,
                                          new Func1Request { Question = exceptionQuestion },
                                          CancellationToken.None);

        // Ensure Func3Async is executing when we close the stream
        await _func3Barrier.SignalAndWaitAsync();
        serverStream.Close();

        // FIXME Should throw WebSocketRpcException but currently does not
        await Assert.ThrowsAnyAsync<Exception>(async () => await invokeTask);
        
        Assert.True(serverRpc.IsClosingStarted);
        await Assert.ThrowsAsync<WebSocketRpcException>(
            async () => await serverRpc.WaitForCloseAsync());
        Assert.True(serverRpc.HasClosed);

        Assert.True(clientRpc.IsClosingStarted);
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await clientRpc.WaitForCloseAsync());
        Assert.True(clientRpc.HasClosed);

        // The remote implementation should get cancelled too
        await _func3Result.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
