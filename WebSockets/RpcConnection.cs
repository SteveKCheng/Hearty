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
        protected RpcConnection(RpcRegistry registry)
        {
            Registry = registry;
        }
    }
}
