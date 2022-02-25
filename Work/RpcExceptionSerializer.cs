using System;
using System.Buffers;
using MessagePack;
using Hearty.WebSockets;
using Hearty.Common;

namespace Hearty.Work
{
    /// <summary>
    /// Serializes exceptions for RPC using the payload type <see cref="ExceptionPayload" />.
    /// </summary>
    public sealed class RpcExceptionSerializer : IRpcExceptionSerializer
    {
        private readonly MessagePackSerializerOptions _serializeOptions;

        /// <summary>
        /// Construct with the specified MessagePack serialization options.
        /// </summary>
        /// <param name="serializeOptions">
        /// MessagePack serialization options which should be consistent
        /// with those used for non-exceptional messages.
        /// </param>
        public RpcExceptionSerializer(MessagePackSerializerOptions serializeOptions)
            => _serializeOptions = serializeOptions;

        object IRpcExceptionSerializer.PreparePayload(Exception exception)
            => ExceptionPayload.CreateFromException(exception);

        Exception IRpcExceptionSerializer.DeserializeToException(in ReadOnlySequence<byte> payload)
        {
            var body = MessagePackSerializer.Deserialize<ExceptionPayload>(payload, _serializeOptions);
            return new RemoteWorkException(body);
        }

        void IRpcExceptionSerializer.SerializePayload(IBufferWriter<byte> writer, object payload)
            => MessagePackSerializer.Serialize<ExceptionPayload>(
                writer, (ExceptionPayload)payload, _serializeOptions);
    }
}
