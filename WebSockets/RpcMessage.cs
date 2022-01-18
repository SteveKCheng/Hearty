using System;
using System.Buffers;
using System.IO;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Represents a pending message in <see cref="WebSocketRpc"/>'s internal
    /// channel.
    /// </summary>
    /// <typeparam name="TReply">User-defined type for the reply outputs. </typeparam>
    internal abstract class RpcMessage
    {
        public abstract void PackMessage(IBufferWriter<byte> writer);

        public abstract void ProcessReplyMessage(in ReadOnlySequence<byte> payload, 
                                                 bool isException);

        public ushort TypeCode { get; }

        public RpcMessageKind Kind { get; }

        public uint ReplyId { get; }

        protected RpcMessage(ushort typeCode, RpcMessageKind kind)
        {
            TypeCode = typeCode;
            Kind = kind;
        }

        protected RpcMessage(ushort typeCode, RpcMessageKind kind, uint replyId)
            : this(typeCode, kind)
        {
            ReplyId = replyId;
        }
    }

    internal readonly struct RpcMessageHeader
    {
        public RpcMessageKind Kind { get; }

        /// <summary>
        /// User-specified type code for the message used
        /// to dispatch to the correct function to process it.
        /// </summary>
        public ushort TypeCode { get; }

        /// <summary>
        /// Sequence number for the message to distinguish replies to
        /// different requests.
        /// </summary>
        public uint Id { get; }

        public RpcMessageHeader(RpcMessageKind kind, ushort typeCode, uint id)
        {
            Kind = kind;
            TypeCode = typeCode;
            Id = id;
        }

        /// <summary>
        /// Encode the information in this instance as an 8-byte header
        /// on a RPC message.
        /// </summary>
        public ulong Pack()
        {
            ulong v = 0;
            v |= (uint)Kind;
            v |= ((uint)(ushort)TypeCode) << 16;
            v |= ((ulong)Id) << 32;
            return v;
        }

        /// <summary>
        /// Decode the 8-byte header on a RPC message.
        /// </summary>
        public static RpcMessageHeader Unpack(ulong v)
        {
            var kind = (ushort)v;
            var typeCode = ((uint)v) >> 16;
            var id = (uint)(v >> 32);
            if (kind >= (ushort)RpcMessageKind.Invalid)
                throw new InvalidDataException("Message kind from incoming RPC message is invalid. ");
            return new RpcMessageHeader((RpcMessageKind)kind, (ushort)typeCode, id);
        }
    }

    internal enum RpcMessageKind : ushort
    {
        Request = 0x0,
        Cancellation = 0x1,
        NormalReply = 0x2,
        ExceptionalReply = 0x3,
        Invalid
    }
}
