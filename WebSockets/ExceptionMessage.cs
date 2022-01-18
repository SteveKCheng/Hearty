using MessagePack;
using System;
using System.Buffers;

namespace JobBank.WebSockets
{
    [MessagePackObject]
    public struct ExceptionMessagePayload
    {
        [Key("class")]
        public string? Class { get; set; }

        [Key("description")]
        public string? Description { get; set; }

        [Key("source")]
        public string? Source { get; set; }

        [Key("stackTrace")]
        public string? StackTrace { get; set; }
    }

    internal class ExceptionMessage : RpcMessage
    {
        public ExceptionMessagePayload Body { get; }

        public ExceptionMessage(ushort typeCode, Exception exception, uint replyId)
            : base(typeCode, RpcMessageKind.ExceptionalReply, replyId)
        {
            Body = new ExceptionMessagePayload
            {
                Class = exception.GetType().FullName,
                Description = exception.Message,
                Source = exception.Source,
                StackTrace = exception.StackTrace,
            };
        }

        public override void PackMessage(IBufferWriter<byte> writer)
        {
            MessagePackSerializer.Serialize(writer, Body, options: null);
        }

        public override void ProcessReplyMessage(in ReadOnlySequence<byte> payload, bool isException)
             => throw new InvalidOperationException();
    }
}
