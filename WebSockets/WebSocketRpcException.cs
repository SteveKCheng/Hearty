using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.WebSockets
{
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
