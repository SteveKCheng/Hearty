using Hearty.Carp;
using MessagePack;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Work
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
    public sealed class WorkerHost : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Type code for the "RegisterWorker" function
        /// in the RPC protocol.
        /// </summary>
        public static readonly ushort TypeCode_RegisterWorker = 0x1;

        /// <summary>
        /// Type code for the "RunJob" function
        /// in the RPC protocol.
        /// </summary>
        public static readonly ushort TypeCode_RunJob = 0x2;

        private static ValueTask<JobReplyMessage> RunJobImplAsync(
            JobRequestMessage request,
            RpcConnection connection,
            CancellationToken cancellationToken)
        {
            var self = (WorkerHost)connection.State!;
            return self._impl.RunJobAsync(request, cancellationToken);
        }

        /// <summary>
        /// Serialization options for MessagePack used by the RPC protocol.
        /// </summary>
        public static MessagePackSerializerOptions SerializeOptions { get; }
            = CreateSerializeOptions();

        private static MessagePackSerializerOptions CreateSerializeOptions()
        {
            var resolver = MessagePack.Resolvers.CompositeResolver.Create(
                            MessagePack.Resolvers.DynamicEnumAsStringResolver.Instance,
                            MessagePack.Resolvers.StandardResolver.Instance
                        );

            var serializeOptions = RpcRegistry.StandardSerializeOptions
                                              .WithResolver(resolver);

            return serializeOptions;
        }

        private static RpcRegistry CreateWorkerRpcRegistry()
        {
            var registry = new RpcRegistry(new RpcExceptionSerializer(SerializeOptions), SerializeOptions);
            registry.Add<JobRequestMessage, JobReplyMessage>(
                WorkerHost.TypeCode_RunJob, RunJobImplAsync);

            return registry;
        }

        private static readonly RpcRegistry _rpcRegistry = CreateWorkerRpcRegistry();

        private readonly WebSocketRpc _rpc;
        private readonly IJobSubmission _impl;

        private WorkerHost(string name,
                           WebSocket webSocket,
                           Func<RpcConnection, IJobSubmission> impl)
        {
            _rpc = new WebSocketRpc(webSocket, _rpcRegistry, this);
            _impl = impl.Invoke(_rpc);
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
        /// Hearty protocol over WebSockets.
        /// </summary>
        /// <param name="implFactory">
        /// Provides the implementation of the job submission functions 
        /// (that run from the local process).  It can optionally
        /// communicate with the job server through two-way RPC.
        /// </param>
        /// <param name="settings">
        /// Settings that the new worker is registered with in the
        /// job server.
        /// </param>
        /// <param name="webSocket">
        /// WebSocket connection to the job server to register the 
        /// new worker host with.  If this method succeeds, the
        /// new instance of <see cref="WorkerHost" /> will take
        /// ownership of this connection.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the operation.
        /// </param>
        public static async Task<WorkerHost>
            StartAsync(Func<RpcConnection, IJobSubmission> implFactory,
                       RegisterWorkerRequestMessage settings,
                       WebSocket webSocket,
                       CancellationToken cancellationToken)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                throw new WebSocketException(WebSocketError.InvalidState,
                    "The WebSocket connection passed to WorkerHost.StartAsync must be open. ");
            }

            var self = new WorkerHost(settings.Name, webSocket, implFactory);

            var reply = await self.RegisterWorkerAsync(settings, cancellationToken)
                                  .ConfigureAwait(false);

            if (reply.Status != RegisterWorkerReplyStatus.Ok)
                throw new Exception("Failed to register worker host with job server. ");

            return self;
        }

        /// <summary>
        /// Register the new worker host and begin accepting work
        /// over a newly established RPC connection using the
        /// Hearty protocol over WebSockets.
        /// </summary>
        /// <param name="impl">
        /// The implementation of the job submission functions 
        /// (that run from the local process).  
        /// </param>
        /// <param name="settings">
        /// Settings that the new worker is registered with in the
        /// job server.
        /// </param>
        /// <param name="webSocket">
        /// WebSocket connection to the job server to register the 
        /// new worker host with.  If this method succeeds, the
        /// new instance of <see cref="WorkerHost" /> will take
        /// ownership of this connection.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the operation.
        /// </param>
        public static Task<WorkerHost>
            StartAsync(IJobSubmission impl,
                       RegisterWorkerRequestMessage settings,
                       WebSocket webSocket,
                       CancellationToken cancellationToken)
            => StartAsync(_ => impl, settings, webSocket, cancellationToken);

        /// <summary>
        /// The string used to distinguish the sub-protocol over
        /// WebSockets used for worker registration and job submission.
        /// </summary>
        public static readonly string WebSocketsSubProtocol = "Hearty.Work";

        /// <summary>
        /// Conventional path for the WebSockets endpoint.
        /// </summary>
        public static readonly string WebSocketsDefaultPath = "/ws/worker";

        /// <summary>
        /// Register the new worker host and begin accepting work
        /// by connecting to a Hearty server over WebSockets.
        /// </summary>
        /// <param name="implFactory">
        /// Provides the implementation of the job submission functions 
        /// (that run from the local process).  It can optionally
        /// communicate with the job server through two-way RPC.
        /// </param>
        /// <param name="settings">
        /// Settings that the new worker is registered with in the
        /// job server.
        /// </param>
        /// <param name="server">
        /// WebSocket URL to the Hearty server.
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
            (Func<RpcConnection, IJobSubmission> implFactory,
             RegisterWorkerRequestMessage settings,
             Uri server,
             Action<ClientWebSocketOptions>? webSocketOptionsSetter,
             CancellationToken cancellationToken)
        {
            var webSocket = new ClientWebSocket();

            try
            {
                webSocket.Options.AddSubProtocol(WebSocketsSubProtocol);
                webSocketOptionsSetter?.Invoke(webSocket.Options);

                await webSocket.ConnectAsync(server, cancellationToken)
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

                return await StartAsync(implFactory, settings, webSocket, cancellationToken)
                                .ConfigureAwait(false);
            }
            catch
            {
                webSocket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Register the new worker host and begin accepting work
        /// by connecting to a Hearty server over WebSockets.
        /// </summary>
        /// <param name="impl">
        /// The implementation of the job submission functions 
        /// (that run from the local process).  
        /// </param>
        /// <param name="settings">
        /// Settings that the new worker is registered with in the
        /// job server.
        /// </param>
        /// <param name="server">
        /// WebSocket URL to the Hearty server.
        /// </param>
        /// <param name="webSocketOptionsSetter">
        /// If null, this action is invoked to customize the WebSocket
        /// connection that is about to be made.  New 
        /// WebSocket sub-protocols should not be added by this action.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to stop connecting and cancel the operation.
        /// </param>
        public static Task<WorkerHost> ConnectAndStartAsync
            (IJobSubmission impl,
             RegisterWorkerRequestMessage settings,
             Uri server,
             Action<ClientWebSocketOptions>? webSocketOptionsSetter,
             CancellationToken cancellationToken)
            => ConnectAndStartAsync(_ => impl, settings, server,
                                    webSocketOptionsSetter, cancellationToken);

        /// <summary>
        /// Register the new worker host and begin accepting work
        /// by connecting to a Hearty server over WebSockets.
        /// </summary>
        /// <param name="implFactory">
        /// Provides the implementation of the job submission functions 
        /// (that run from the local process).  It can optionally
        /// communicate with the job server through two-way RPC.
        /// </param>
        /// <param name="settings">
        /// Settings that the new worker is registered with in the
        /// job server.
        /// </param>
        /// <param name="server">
        /// WebSocket URL to the Hearty server.
        /// </param>
        /// <param name="webSocketOptionsSetter">
        /// If null, this action is invoked to customize the WebSocket
        /// connection that is about to be made.  New 
        /// WebSocket sub-protocols should not be added by this action.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to stop connecting and cancel the operation.
        /// </param>
        public static Task<WorkerHost> ConnectAndStartAsync
            (Func<RpcConnection, IJobSubmission> implFactory,
             RegisterWorkerRequestMessage settings,
             string server,
             Action<ClientWebSocketOptions>? webSocketOptionsSetter,
             CancellationToken cancellationToken)
            => ConnectAndStartAsync(implFactory,
                                    settings,
                                    new Uri(server),
                                    webSocketOptionsSetter,
                                    cancellationToken);

        /// <summary>
        /// Register the new worker host and begin accepting work
        /// by connecting to a Hearty server over WebSockets.
        /// </summary>
        /// <param name="impl">
        /// The implementation of the job submission functions 
        /// (that run from the local process).  
        /// </param>
        /// <param name="settings">
        /// Settings that the new worker is registered with in the
        /// job server.
        /// </param>
        /// <param name="server">
        /// WebSocket URL to the Hearty server.
        /// </param>
        /// <param name="webSocketOptionsSetter">
        /// If null, this action is invoked to customize the WebSocket
        /// connection that is about to be made.  New 
        /// WebSocket sub-protocols should not be added by this action.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to stop connecting and cancel the operation.
        /// </param>
        public static Task<WorkerHost> ConnectAndStartAsync
            (IJobSubmission impl,
             RegisterWorkerRequestMessage settings,
             string server,
             Action<ClientWebSocketOptions>? webSocketOptionsSetter,
             CancellationToken cancellationToken)
            => ConnectAndStartAsync(_ => impl,
                                    settings,
                                    new Uri(server),
                                    webSocketOptionsSetter,
                                    cancellationToken);

        /// <summary>
        /// Cancel all pending requests and tear down the worker.
        /// </summary>
        public void Dispose()
        {
            DisposeAsync().AsTask().Wait();
        }

        /// <summary>
        /// Cancel all pending requests and tear down the worker.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            // RpcConnection will cancel all pending requests
            // when it is disposed, through the CancellationToken
            // passed to RunJobImplAsync.
            await _rpc.DisposeAsync().ConfigureAwait(false);

            await _impl.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Derive the WebSocket URL 
        /// from the URL for the hosting Hearty Web server.
        /// </summary>
        /// <param name="siteUrl">
        /// Absolute URL with the "http" or "https" scheme, plus
        /// the appropriate path base appended.
        /// </param>
        /// <returns>
        /// The WebSocket URL, with the "ws" or "wss" scheme.
        /// </returns>
        public static Uri DeriveWebSocketUrl(string siteUrl)
            => DeriveWebSocketUrl(new Uri(siteUrl));

        /// <summary>
        /// Derive the WebSocket URL 
        /// from the URL for the hosting Hearty Web server.
        /// </summary>
        /// <param name="siteUrl">
        /// Absolute URL with the "http" or "https" scheme, plus
        /// the appropriate path base appended.
        /// </param>
        /// <returns>
        /// The WebSocket URL, with the "ws" or "wss" scheme.
        /// </returns>
        public static Uri DeriveWebSocketUrl(Uri siteUrl)
        {
            if (!siteUrl.IsAbsoluteUri)
                throw new ArgumentException("URL must be absolute, but is not. ", nameof(siteUrl));

            bool secure;
            if (string.Equals(siteUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                secure = true;
            else if (string.Equals(siteUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                secure = false;
            else
                throw new ArgumentException("URL must have the 'http' or 'https' scheme, but does not. ", nameof(siteUrl));

            var builder = new UriBuilder(siteUrl);

#if NET6_0_OR_GREATER
            builder.Scheme = secure ? Uri.UriSchemeWss : Uri.UriSchemeNews;
#else
            builder.Scheme = secure ? "wss" : "ws";
#endif

            ReadOnlySpan<char> subpath = builder.Path.EndsWith('/')
                                            ? WorkerHost.WebSocketsDefaultPath[1..]
                                            : WorkerHost.WebSocketsDefaultPath;

            builder.Path = string.Concat(builder.Path, subpath);

            return builder.Uri;
        }
    }
}
