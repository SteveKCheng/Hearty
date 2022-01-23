using System;
using System.Net.WebSockets;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Reports an exceptional or error condition from making
    /// or receiving a remote procedure call over WebSockets.
    /// </summary>
    public class WebSocketRpcException : Exception
    {
        public WebSocketCloseStatus CloseStatus { get; }

        public WebSocketRpcException(WebSocketCloseStatus status)
            : base($"WebSocketCloseStatus = {status}")
        {
            CloseStatus = status;
        }
    }
}
