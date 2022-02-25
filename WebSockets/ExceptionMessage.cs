using MessagePack;
using System;
using System.Buffers;

namespace Hearty.WebSockets
{
    internal sealed class ExceptionMessage : RpcMessage
    {
        public object Payload { get; }

        private readonly IRpcExceptionSerializer _exceptionSerializer;

        public ExceptionMessage(ushort typeCode, uint replyId, RpcRegistry registry, Exception exception)
            : base(RpcMessageKind.ExceptionalReply, typeCode, replyId)
        {
            _exceptionSerializer = registry._exceptionSerializer;
            Payload = _exceptionSerializer.PreparePayload(exception);
        }

        public override void PackPayload(IBufferWriter<byte> writer)
            => _exceptionSerializer.SerializePayload(writer, Payload);
    }
}
