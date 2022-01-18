using MessagePack;
using System;
using System.Buffers;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Holds a reply before it gets sent over WebSockets.
    /// </summary>
    /// <typeparam name="TReply"></typeparam>
    internal class ReplyMessage<TReply> : RpcMessage
    {
        public ReplyMessage(ushort typeCode, TReply body, uint replyId)
            : base(typeCode, RpcMessageKind.NormalReply, replyId)
        {
            Body = body;
        }

        public TReply Body { get; }

        public override void PackMessage(IBufferWriter<byte> writer)
        {
            MessagePackSerializer.Serialize(writer, Body, options: null);
        }

        public override void ProcessReplyMessage(in ReadOnlySequence<byte> payload, bool isException)
             => throw new InvalidOperationException();
    }
}