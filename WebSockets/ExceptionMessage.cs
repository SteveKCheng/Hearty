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
    public class ExceptionMessagePayload
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
        public object Payload { get; }

        private readonly IExceptionSerializer _exceptionSerializer;

        public ExceptionMessage(ushort typeCode, uint replyId, RpcRegistry registry, Exception exception)
            : base(RpcMessageKind.ExceptionalReply, typeCode, replyId)
        {
            _exceptionSerializer = registry._exceptionSerializer;
            Payload = _exceptionSerializer.PreparePayload(exception);
        }

        public override void PackPayload(IBufferWriter<byte> writer)
            => _exceptionSerializer.SerializePayload(writer, Payload);
    }

    internal sealed class ExceptionSerializer : IExceptionSerializer
    {
        private readonly MessagePackSerializerOptions _serializeOptions;

        public ExceptionSerializer(MessagePackSerializerOptions serializeOptions)
            => _serializeOptions = serializeOptions;

        public object PreparePayload(Exception exception)
            => ExceptionMessagePayload.CreateFromException(exception);

        public Exception DeserializeToException(in ReadOnlySequence<byte> payload)
        {
            var body = MessagePackSerializer.Deserialize<ExceptionMessagePayload>(payload, _serializeOptions);
            return new Exception(body.Description);
        }

        public void SerializePayload(IBufferWriter<byte> writer, object payload)
            => MessagePackSerializer.Serialize<ExceptionMessagePayload>(
                writer, (ExceptionMessagePayload)payload, _serializeOptions);
    }
}
