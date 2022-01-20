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

        /// <param name="name">
        /// The name of this host to report to the job server.
        /// </param>
        /// <param name="concurrency">
        /// The degree of concurrency allowed for the new worker host.
        /// </param>
        /// <param name="webSocket">
        /// WebSocket connection to the job server to register the 
        /// new worker host with.
        /// </param>
        /// <param name="impl">
        /// Implementation of the job submission functions (that run from the
        /// local process).
        /// </param>
        public static async Task<WorkerHost> StartAsync(string name, 
                                                        int concurrency,
                                                        WebSocket webSocket,
                                                        IJobSubmission impl,
                                                        CancellationToken cancellationToken)
        {
            if (concurrency < 0 || concurrency > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(concurrency));

            var self = new WorkerHost(name, webSocket, impl);
            
            var reply = await self.RegisterWorkerAsync(new RegisterWorkerRequestMessage
            {
                Name = name,
                Concurrency = (ushort)concurrency
            }, cancellationToken).ConfigureAwait(false);

            if (reply.Status != RegisterWorkerReplyStatus.Ok)
                throw new Exception("Failed to register worker host with job server. ");

            return self;
        }
    }
}
