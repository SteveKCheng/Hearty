using MessagePack;
using System;
using System.Buffers;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Holds a reply before it gets sent over WebSockets.
    /// </summary>
    /// <typeparam name="TReply">User-defined type for the reply outputs. </typeparam>
    internal sealed class ReplyMessage<TReply> : RpcMessage
    {
        public ReplyMessage(ushort typeCode, TReply body, uint replyId)
            : base(typeCode, RpcMessageKind.NormalReply, replyId)
        {
            Body = body;
        }

        public TReply Body { get; }

        public override void PackPayload(IBufferWriter<byte> writer)
        {
            MessagePackSerializer.Serialize(writer, Body, options: null);
        }

        public override void ProcessReply(in ReadOnlySequence<byte> payload, bool isException)
             => throw new InvalidOperationException();
    }
}