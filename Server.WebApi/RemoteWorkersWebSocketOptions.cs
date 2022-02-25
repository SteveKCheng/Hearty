using Hearty.Work;
using Microsoft.AspNetCore.Http;
using System;

namespace Hearty.Server.WebApi
{
    /// <summary>
    /// Options for the remote workers' endpoint speaking a WebSocket protocol.
    /// </summary>
    public class RemoteWorkersWebSocketOptions : WebSocketAcceptContext
    {
        /// <summary>
        /// The fixed sub-protocol for the remote workers' WebSocket service.
        /// It may not be overridden.
        /// </summary>
        public sealed override string? SubProtocol
        {
            get => WorkerHost.WebSocketsSubProtocol;
            set => throw new InvalidOperationException("The WebSocket protocol may not be overridden for the remote workers endpoint. ");
        }

        /// <summary>
        /// Create an instance with default settings
        /// </summary>
        public RemoteWorkersWebSocketOptions()
        {
            DangerousEnableCompression = true;
            DisableServerContextTakeover = true;
            RegistrationTimeout = TimeSpan.FromSeconds(60);
        }

        /// <summary>
        /// The time interval allowed for the 
        /// remote worker to register itself after the WebSocket
        /// connection is accepted.
        /// </summary>
        public TimeSpan RegistrationTimeout { get; set; }
    }
}
