using MessagePack;
using System;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Represents a pending message in <see cref="WebSocketRpc"/>'s internal
    /// channel.
    /// </summary>
    /// <typeparam name="TReply">User-defined type for the reply outputs. </typeparam>
    internal abstract class RpcMessage
    {
        public abstract void PackMessage(ref MessagePackWriter writer, uint id);

        public abstract void ProcessReplyMessage(ref MessagePackReader reader, short typeCode);

        public short TypeCode { get; }

        public bool IsReply { get; }

        public uint ReplyMessageId { get; }

        protected RpcMessage(short typeCode)
        {
            TypeCode = typeCode;
        }

        protected RpcMessage(short typeCode, uint replyMessageId)
            : this(typeCode)
        {
            IsReply = true;
            ReplyMessageId = replyMessageId;
        }
    }
}