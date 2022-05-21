using System;
using System.Net.WebSockets;

namespace Hearty.Carp;

/// <summary>
/// Reports an exceptional or error condition from making
/// or receiving a remote procedure call over WebSockets.
/// </summary>
public class WebSocketRpcException : Exception
{
    /// <summary>
    /// The reason reported for the WebSocket connection closing.
    /// </summary>
    public WebSocketCloseStatus CloseStatus { get; }

    /// <summary>
    /// Constructor.
    /// </summary>
    public WebSocketRpcException(WebSocketCloseStatus status)
        : base($"WebSocketCloseStatus = {status}")
    {
        CloseStatus = status;
    }
}
