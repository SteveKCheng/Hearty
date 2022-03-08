using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Scheduling;
using Hearty.Carp;
using Hearty.Work;

namespace Hearty.Server
{
    /// <summary>
    /// Implements a WebSocket endpoint that remote ("slave") hosts 
    /// connect to so they can accept jobs to run.
    /// </summary>
    public static class RemoteWorkerService
    {
        private static readonly RpcRegistry _rpcRegistry = CreateBankRpcRegistry();

        private static RpcRegistry CreateBankRpcRegistry()
        {
            var registry = new RpcRegistry(new RpcExceptionSerializer(WorkerHost.SerializeOptions), WorkerHost.SerializeOptions);
            registry.Add<RegisterWorkerRequestMessage, RegisterWorkerReplyMessage>(
                WorkerHost.TypeCode_RegisterWorker,
                (r, c, t) => ValueTask.FromResult(RegisterRemoteWorkerImpl(r, c, t)));
            return registry;
        }

        private static RegisterWorkerReplyMessage
            RegisterRemoteWorkerImpl(RegisterWorkerRequestMessage request,
                                     RpcConnection connection,
                                     CancellationToken cancellationToken)
        {
            var state = (PreInitState)connection.State!;
            bool success = false;

            try
            {
                var reply = new RegisterWorkerReplyMessage();

                if (Interlocked.Exchange(ref state.IsRegistered, 1) != 0)
                {
                    reply.Status = RegisterWorkerReplyStatus.ConcurrentRegistration;
                    return reply;
                }

                var distribution = state.WorkerDistribution;
                var workerImpl = new RemoteWorkerProxy(request.Name, connection);

                success = distribution.TryCreateWorker(workerImpl,
                                                       request.Concurrency,
                                                       out var worker);

                if (success)
                    state.SetResult(worker!);

                reply.Status = success ? RegisterWorkerReplyStatus.Ok
                                       : RegisterWorkerReplyStatus.NameAlreadyExists;
                return reply;
            }
            catch (Exception e)
            {
                state.SetException(e);
                throw;
            }
            finally
            {
                if (!success)
                    state.IsRegistered = 0;
            }
        }

        private sealed class PreInitState : TaskCompletionSource<IDistributedWorker<PromisedWork>>
        {
            public readonly WorkerDistribution<PromisedWork, PromiseData> WorkerDistribution;

            public PreInitState(WorkerDistribution<PromisedWork, PromiseData> distribution)
                => WorkerDistribution = distribution
                     ?? throw new ArgumentNullException(nameof(distribution));

            public int IsRegistered;

            public WebSocketRpc? Rpc;
        }

        /// <summary>
        /// Expect a remote host to register itself as a worker
        /// to the job distribution system.
        /// </summary>
        /// <param name="distribution">The system of (remote) workers. </param>
        /// <param name="webSocket">
        /// Newly opened WebSocket connection.
        /// It should use <see cref="WorkerHost.WebSocketsSubProtocol" />
        /// as its sub-protocol.
        /// </param>
        /// <param name="cancellationToken">
        /// When triggered, cancels the waiting.  It can be used to time
        /// out the idle connection when the remote host is not doing
        /// anything.
        /// </param>
        /// <returns>
        /// The first member of the pair is the worker,
        /// as a member of <paramref name="distribution" />.
        /// The second member is an asynchronous task that completes
        /// when the WebSocket connection is shut down, provided
        /// for interoperability with frameworks like ASP.NET Core,
        /// where the implementation of WebSocket endpoint must not
        /// return to the framework until the WebSocket connection
        /// is ready to be closed.
        /// </returns>
        public static async 
            Task<(IDistributedWorker<PromisedWork> Worker, Task CloseTask)> 
            AcceptHostAsync(
                WorkerDistribution<PromisedWork, PromiseData> distribution,
                WebSocket webSocket,
                CancellationToken cancellationToken)
        {
            var state = new PreInitState(distribution);
            state.Rpc = new WebSocketRpc(webSocket, _rpcRegistry, state);

            CancellationTokenRegistration cancelRegistration = default;
            if (cancellationToken.CanBeCanceled)
            {
                cancelRegistration = cancellationToken.Register(
                    s =>
                    {
                        var self = (PreInitState)s!;
                        if (self.TrySetCanceled())
                            self.Rpc!.Dispose();
                    },
                    state,
                    useSynchronizationContext: false);
            }

            using var _ = cancelRegistration;
            var worker = await state.Task;
            return (worker, state.Rpc.WaitForCloseAsync());
        }
    }
}
