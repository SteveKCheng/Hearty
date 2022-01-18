using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.WebSockets
{
    /// <summary>
    /// An abstract connection capable of bi-directional remote procedure
    /// call as implemented by this library.
    /// </summary>
    public abstract class RpcConnection
    {
        /// <summary>
        /// Directs how payloads and messages on this RPC connection 
        /// are processed.
        /// </summary>
        public RpcRegistry Registry { get; }

        /// <summary>
        /// Reference to an arbitrary object that can be associated
        /// to this connection.
        /// </summary>
        /// <remarks>
        /// This property is provided so that the application can
        /// look up and store application-specific information
        /// for this connection.
        /// </remarks>
        public object? State { get; }

        internal ValueTask SendReplyAsync<TReply>(ushort typeCode, uint id, TReply reply)
            => SendMessageAsync(new ReplyMessage<TReply>(typeCode, id, Registry, reply));

        internal ValueTask SendExceptionAsync(ushort typeCode, uint id, Exception e)
            => SendMessageAsync(new ExceptionMessage(typeCode, id, Registry, e));

        internal ValueTask SendCancellationAsync(ushort typeCode, uint id)
            => SendMessageAsync(new CancellationMessage(typeCode, id));

        private protected abstract ValueTask SendMessageAsync(RpcMessage message);

        /// <summary>
        /// Sets basic/common information about the RPC connection
        /// on construction.
        /// </summary>
        /// <param name="registry">The reference that is stored
        /// in <see cref="Registry" />.
        /// </param>
        /// <param name="state">The reference that is assigned
        /// to <see cref="State" />.
        /// </param>
        protected RpcConnection(RpcRegistry registry, object? state)
        {
            Registry = registry;
            State = state;
        }
    }
}
