using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Scheduling;
using Hearty.Carp;
using Hearty.Work;

namespace Hearty.Server;

/// <summary>
/// Registry for the server side of the RPC protocol for distributing job requests.
/// </summary>
/// <remarks>
/// An instance of this class can be modified to support custom
/// functions over the same RPC connection used to communicate
/// with the job worker.  <see cref="RemoteWorkerService" /> 
/// requires this sub-class of <see cref="RpcRegistry" />, 
/// and not the base class, so that
/// the constructor of this class can enforce the minimum
/// requirements for the job-serving protocol.
/// </remarks>
public class JobServerRpcRegistry : RpcRegistry
{
    /// <summary>
    /// Construct with the basic minimum settings.
    /// </summary>
    public JobServerRpcRegistry()
        : base(new RpcExceptionSerializer(WorkerHost.SerializeOptions), WorkerHost.SerializeOptions)
    {
        this.Add<RegisterWorkerRequestMessage, RegisterWorkerReplyMessage>(
            WorkerHost.TypeCode_RegisterWorker,
            (r, c, t) => ValueTask.FromResult(RemoteWorkerService.RegisterRemoteWorkerImpl(r, c, t)));
    }

    internal static readonly JobServerRpcRegistry DefaultInstance = new JobServerRpcRegistry();
}

/// <summary>
/// Implements a WebSocket endpoint that remote ("slave") hosts 
/// connect to so they can accept jobs to run.
/// </summary>
public static class RemoteWorkerService
{
    internal static RegisterWorkerReplyMessage
        RegisterRemoteWorkerImpl(RegisterWorkerRequestMessage request,
                                 RpcConnection connection,
                                 CancellationToken cancellationToken)
    {
        var state = (RemoteWorkerService.PreInitState)connection.State!;
        bool success = false;

        var reply = new RegisterWorkerReplyMessage();

        if (Interlocked.Exchange(ref state.IsRegistered, 1) != 0)
        {
            reply.Status = RegisterWorkerReplyStatus.ConcurrentRegistration;
            return reply;
        }

        try
        {
            var distribution = state.WorkerDistribution;
            var workerImpl = new RemoteWorkerProxy(request.Name, connection);

            success = distribution.TryCreateWorker(workerImpl,
                                                   request.Concurrency,
                                                   out var worker);

            if (success)
            {
                state.SetResult(worker!);
                workerImpl.StartHeartbeats(period: TimeSpan.FromMinutes(1),
                                           timeout: TimeSpan.FromMinutes(1));
            }

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
    /// <param name="rpcRegistry">
    /// The RPC registry that will be used to accept connections.  
    /// Custom functions can be registered into that registry, allowing 
    /// the worker to call back into the job server while it is executing work,
    /// i.e. before the worker sends its reply message. 
    /// Complex jobs may require the job worker to coordinate
    /// with the job server.  For example, a worker can ask the 
    /// job server to de-duplicate partial work against other jobs.
    /// The RPC registry can also be configured for serialization
    /// of custom types with MessagePack.  If this argument is null,
    /// it is as if a default-constructed <see cref="JobServerRpcRegistry" />
    /// has been specified.
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
            JobServerRpcRegistry? rpcRegistry = null,
            CancellationToken cancellationToken = default)
    {
        var state = new PreInitState(distribution);

        rpcRegistry ??= JobServerRpcRegistry.DefaultInstance;

        state.Rpc = new WebSocketRpc(webSocket, rpcRegistry, state);

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
