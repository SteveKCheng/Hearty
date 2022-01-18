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
        internal ValueTask SendReplyAsync<TReply>(ushort typeCode, uint id, TReply reply)
            => SendMessageAsync(new ReplyMessage<TReply>(typeCode, reply, id));

        internal ValueTask SendExceptionAsync(ushort typeCode, uint id, Exception e)
            => SendMessageAsync(new ExceptionMessage(typeCode, e, id));

        internal ValueTask SendCancellationAsync(ushort typeCode, uint id)
            => SendMessageAsync(new CancellationMessage(typeCode, id));

        private protected abstract ValueTask SendMessageAsync(RpcMessage message);
    }
}
