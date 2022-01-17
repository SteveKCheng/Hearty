using MessagePack;
using System;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Holds a reply before it gets sent over WebSockets.
    /// </summary>
    /// <typeparam name="TReply"></typeparam>
    internal class ReplyMessage<TReply> : RpcMessage
    {
        public ReplyMessage(short typeCode, TReply body, uint replyMessageId)
            : base(typeCode, replyMessageId)
        {
            Body = body;
        }

        public TReply Body { get; }

        public override void PackMessage(ref MessagePackWriter writer, uint id)
        {
            MessagePackSerializer.Serialize(ref writer, Body, options: null);
        }

        public override void ProcessReplyMessage(ref MessagePackReader reader, short typeCode)
             => throw new InvalidOperationException();
    }
}