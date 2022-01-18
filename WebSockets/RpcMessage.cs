using System;
using System.Buffers;
using System.IO;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Represents a pending message in <see cref="WebSocketRpc"/>'s internal
    /// channel.
    /// </summary>
    internal abstract class RpcMessage
    {
        /// <summary>
        /// Serializes the payload of the message into buffers
        /// for transmission.
        /// </summary>
        /// <remarks>
        /// The header of the message is written by the caller
        /// and must not be written by this method.
        /// </remarks>
        /// <param name="writer">
        /// Provides buffers to write the payload into.
        /// </param>
        public abstract void PackPayload(IBufferWriter<byte> writer);

        /// <summary>
        /// Accepts and processes the reply message when
        /// it is received, if this instance represents a request message.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// This instance does not represent a RPC request, i.e.
        /// <see cref="Kind" /> is not <see cref="RpcMessageKind.Request" />.
        /// </exception>
        /// <param name="payload">
        /// The payload of the reply message that this method
        /// would de-serialize.
        /// </param>
        /// <param name="isException">
        /// Whether the payload is for an exceptional result.
        /// The payload for exception results is, by convention,
        /// the MessagePack serialization of <see cref="ExceptionMessagePayload" />.
        /// </param>
        public abstract void ProcessReply(in ReadOnlySequence<byte> payload, 
                                          bool isException);

        /// <summary>
        /// The type code assigned to the message to distinguish 
        /// different types of requests.
        /// </summary>
        /// <remarks>
        /// Any messages related to the original request message,
        /// i.e. replies or cancellations, must be assigned the
        /// same type code as the request.
        /// </remarks>
        public ushort TypeCode { get; }

        /// <summary>
        /// The category of RPC message this instance represents.
        /// </summary>
        public RpcMessageKind Kind { get; }

        /// <summary>
        /// The ID assigned to the message to identify messages
        /// related to their originating requests messages.
        /// </summary>
        public uint ReplyId { get; }

        protected RpcMessage(ushort typeCode, RpcMessageKind kind, uint replyId)
        {
            TypeCode = typeCode;
            Kind = kind;
            ReplyId = replyId;
        }
    }

    internal readonly struct RpcMessageHeader
    {
        /// <summary>
        /// The general category of RPC message.
        /// </summary>
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

    /// <summary>
    /// Categorizes the major kinds of messages that can arrive
    /// on a channel in the RPC framework of this library.
    /// </summary>
    internal enum RpcMessageKind : ushort
    {
        /// <summary>
        /// A remote function is to be invoked.
        /// </summary>
        Request = 0x0,

        /// <summary>
        /// An earlier invocation of a remote function is to be cancelled.
        /// </summary>
        Cancellation = 0x1,

        /// <summary>
        /// An earlier invocation of a remote function returns a non-exceptional
        /// result.
        /// </summary>
        NormalReply = 0x2,

        /// <summary>
        /// An earlier invocation of a remote function failed and
        /// is reporting an exception.
        /// </summary>
        ExceptionalReply = 0x3,

        /// <summary>
        /// Not a valid entry; used to mark the end of the range of valid values.
        /// </summary>
        Invalid
    }
}
