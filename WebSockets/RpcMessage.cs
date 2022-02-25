using System;
using System.Buffers;
using System.IO;

namespace Hearty.WebSockets
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
        /// The payload for exception results is serialized
        /// by <see cref="IRpcExceptionSerializer" />.
        /// </param>
        public virtual void ProcessReply(in ReadOnlySequence<byte> payload, 
                                         bool isException)
            => throw new InvalidOperationException();

        /// <summary>
        /// Report failure to process or deliver a message, if possible.
        /// </summary>
        /// <remarks>
        /// Implementations of <see cref="RpcMessage" /> that are
        /// expected to report status back to their originator,
        /// such as request messages, can propagate the exception 
        /// passed through here.
        /// </remarks>
        /// <param name="e">
        /// Exception describing the failure. 
        /// </param>
        /// <returns>
        /// True if the exception message has been accepted
        /// or can be ignored; false if the caller needs to report
        /// or log the exception in a more global manner.
        /// </returns>
        public virtual bool Abort(Exception e) => false;

        /// <summary>
        /// Whether this request has been cancelled.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This property needs to be consulted to avoid a race
        /// where a request gets cancelled by the client, entailing
        /// the sending of the cancellation message, before the
        /// original the request message is even sent out.  
        /// </para>
        /// <para>
        /// This property is not used for reply messages.
        /// </para>
        /// </remarks>
        public virtual bool IsCancelled => false;

        /// <summary>
        /// The type code assigned to the message to distinguish 
        /// different types of requests.
        /// </summary>
        /// <remarks>
        /// Any messages related to the original request message,
        /// i.e. replies or cancellations, must be assigned the
        /// same type code as the request.
        /// </remarks>
        public ushort TypeCode => Header.TypeCode;

        /// <summary>
        /// The category of RPC message this instance represents.
        /// </summary>
        public RpcMessageKind Kind => Header.Kind;

        /// <summary>
        /// The ID assigned to the message to identify messages
        /// related to their originating requests messages.
        /// </summary>
        public uint ReplyId => Header.Id;

        /// <summary>
        /// The header of the RPC message to send.
        /// </summary>
        public RpcMessageHeader Header { get; }

        protected RpcMessage(RpcMessageKind kind, ushort typeCode, uint id)
        {
            Header = new RpcMessageHeader(kind, typeCode, id);
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
        /// Acknowledgement of a cancellation request.
        /// </summary>
        /// <remarks>
        /// Such a message is sent by the receiver of the cancellation
        /// request only when it has not yet sent any normal or exceptional
        /// reply.  The acknowledgement or reply allows the requester to 
        /// free up the message ID and associated resources 
        /// taken by the request message.
        /// </remarks>
        AcknowledgedCancellation = 0x4,

        /// <summary>
        /// Not a valid entry; used to mark the end of the range of valid values.
        /// </summary>
        Invalid
    }
}
