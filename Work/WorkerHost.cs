using JobBank.WebSockets;
using MessagePack;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Work
{
    /// <summary>
    /// Makes job submission functions available to a remote client.
    /// </summary>
    /// <remarks>
    /// This class hosts a (local) implementation of <see cref="IJobSubmission" />
    /// over network RPC.  In the context of a job distribution,
    /// the workers are "servers" offering the <see cref="IJobSubmission" />
    /// interface, and the "client" is the central job server that
    /// distributes the jobs.
    /// </remarks>
    public sealed class WorkerHost
    {
        public static readonly ushort TypeCode_RegisterWorker = 0x1;
        public static readonly ushort TypeCode_RunJob = 0x2;

        private static ValueTask<RunJobReplyMessage> RunJobImplAsync(
            RunJobRequestMessage request,
            RpcConnection connection,
            CancellationToken cancellationToken)
        {
            var self = (WorkerHost)connection.State!;
            return self._impl.RunJobAsync(request, cancellationToken);
        }

        internal static MessagePackSerializerOptions SerializeOptions { get; }
            = CreateSerializeOptions();

        private static MessagePackSerializerOptions CreateSerializeOptions()
        {
            var resolver = MessagePack.Resolvers.CompositeResolver.Create(
                            MessagePack.Resolvers.DynamicEnumAsStringResolver.Instance,
                            MessagePack.Resolvers.StandardResolver.Instance
                        );

            var serializeOptions = MessagePackSerializerOptions.Standard
                                .WithSecurity(MessagePackSecurity.UntrustedData)
                                .WithResolver(resolver);

            return serializeOptions;
        }

        private static RpcRegistry CreateServerRpcRegistry()
        {
            var registry = new RpcRegistry(SerializeOptions);
            registry.Add<RunJobRequestMessage, RunJobReplyMessage>(
                WorkerHost.TypeCode_RunJob, RunJobImplAsync);

            return registry;
        }

        public static RpcRegistry RpcRegistry { get; } = CreateServerRpcRegistry();

        private readonly WebSocketRpc _rpc;
        private readonly IJobSubmission _impl;

        private WorkerHost(string name,
                           WebSocket webSocket,
                           IJobSubmission impl)
        {
            _rpc = new WebSocketRpc(webSocket, RpcRegistry, this);
            _impl = impl;
        }

        private ValueTask<RegisterWorkerReplyMessage>
            RegisterWorkerAsync(RegisterWorkerRequestMessage request,
                                CancellationToken cancellationToken)
        {
            return _rpc.InvokeRemotelyAsync<RegisterWorkerRequestMessage,
                                            RegisterWorkerReplyMessage>(
                WorkerHost.TypeCode_RegisterWorker, request, cancellationToken);
        }

        /// <summary>
        /// Register the new worker host and begin accepting work
        /// over a newly established RPC connection using the
        /// JobBank protocol over WebSockets.
        /// </summary>
        /// <param name="settings">
        /// Settings that the new worker is registered with in the
        /// job server.
        /// </param>
        /// <param name="webSocket">
        /// WebSocket connection to the job server to register the 
        /// new worker host with.
        /// </param>
        /// <param name="impl">
        /// Implementation of the job submission functions (that run from the
        /// local process).
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the operation.
        /// </param>
        public static async Task<WorkerHost>
            StartAsync(IJobSubmission impl,
                       RegisterWorkerRequestMessage settings,
                       WebSocket webSocket,
                       CancellationToken cancellationToken)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                throw new WebSocketException(WebSocketError.InvalidState,
                    "The WebSocket connection passed to WorkerHost.StartAsync must be open. ");
            }

            var self = new WorkerHost(settings.Name, webSocket, impl);

            var reply = await self.RegisterWorkerAsync(settings, cancellationToken)
                                  .ConfigureAwait(false);

            if (reply.Status != RegisterWorkerReplyStatus.Ok)
                throw new Exception("Failed to register worker host with job server. ");

            return self;
        }
        
        /// <summary>
        /// The string used to distinguish the sub-protocol over
        /// WebSockets used for worker registration and job submission.
        /// </summary>
        public static readonly string WebSocketsSubProtocol = "JobBank.Work";

        /// <summary>
        /// Register the new worker host and begin accepting work
        /// by connecting to a JobBank server over WebSockets.
        /// </summary>
        /// <param name="settings">
        /// Settings that the new worker is registered with in the
        /// job server.
        /// </param>
        /// <param name="server">
        /// WebSocket URL to the JobBank server.
        /// </param>
        /// <param name="webSocketOptionsSetter">
        /// If null, this action is invoked to customize the WebSocket
        /// connection that is about to be made.  New 
        /// WebSocket sub-protocols should not be added by this action.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to stop connecting and cancel the operation.
        /// </param>
        public static async Task<WorkerHost> ConnectAndStartAsync
            (IJobSubmission impl,
             RegisterWorkerRequestMessage settings,
             string server,
             Action<ClientWebSocketOptions>? webSocketOptionsSetter,
             CancellationToken cancellationToken)
        {
            var webSocket = new ClientWebSocket();

            try
            {
                webSocket.Options.AddSubProtocol(WebSocketsSubProtocol);
                webSocketOptionsSetter?.Invoke(webSocket.Options);
                
                await webSocket.ConnectAsync(new Uri(server), cancellationToken)
                               .ConfigureAwait(false);

                // The WebSocket implementation should already check this
                // but if the user (wrongly) added sub-protocols then
                // they might get negotiated instead.
                if (webSocket.SubProtocol != WebSocketsSubProtocol)
                {
                    throw new WebSocketException(
                        WebSocketError.UnsupportedProtocol,
                        "The WebSocket endpoint being connected to does not speak the expected protocol for job workers. ");
                }

                return await StartAsync(impl, settings, webSocket, cancellationToken)
                                .ConfigureAwait(false);
            }
            catch
            {
                webSocket.Dispose();
                throw;
            }
        }
    }
}
