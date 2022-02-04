using MessagePack;
using System;
using System.Buffers;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Partial representation of a .NET exception 
    /// in a safely serializable form.
    /// </summary>
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

        /// <summary>
        /// Create an instance based on the data in a .NET exception.
        /// </summary>
        public static ExceptionMessagePayload 
            CreateFromException(Exception exception)
        {
            return new ExceptionMessagePayload
            {
                Class = exception.GetType().FullName,
                Description = exception.Message,
                Source = exception.Source,
                StackTrace = exception.StackTrace,
            };
        }
    }

    internal sealed class ExceptionMessage : RpcMessage
    {
        public ExceptionMessagePayload Body { get; }

        private readonly MessagePackSerializerOptions _serializeOptions;

        public ExceptionMessage(ushort typeCode, uint replyId, RpcRegistry registry, Exception exception)
            : base(RpcMessageKind.ExceptionalReply, typeCode, replyId)
        {
            _serializeOptions = registry.SerializeOptions;
            Body = ExceptionMessagePayload.CreateFromException(exception);
        }

        public override void PackPayload(IBufferWriter<byte> writer)
        {
            MessagePackSerializer.Serialize(writer, Body, _serializeOptions);
        }
    }
}
