using MessagePack;
using System;
using System.Buffers;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.WebSockets
{
    internal abstract class RpcMessageProcessor
    {
        public abstract void ProcessMessage(
            in ReadOnlySequence<byte> payload,
            RpcMessageHeader header,
            ChannelWriter<RpcMessage> replyWriter);
    }

    internal class RpcRequestProcessor<TRequest, TReply> : RpcMessageProcessor
    {
        private readonly RpcFunction<TRequest, TReply> _func;

        public RpcRequestProcessor(RpcFunction<TRequest, TReply> func)
        {
            _func = func;
        }

        private async Task ProcessMessageAsync(ReadOnlySequence<byte> payload,
                                               RpcMessageHeader header,
                                               ChannelWriter<RpcMessage> replyWriter)
        {
            try
            {
                var request = MessagePackSerializer.Deserialize<TRequest>(
                                        payload, options: null);

                var replyTask = _func.Invoke(request, default);
                var reply = await replyTask.ConfigureAwait(false);
                var item = new ReplyMessage<TReply>(header.TypeCode, reply, header.Id);
                await replyWriter.WriteAsync(item).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var m = new ExceptionMessage(header.TypeCode, e, header.Id);
                await replyWriter.WriteAsync(m).ConfigureAwait(false);
            }
        }

        public override void ProcessMessage(
            in ReadOnlySequence<byte> payload, 
            RpcMessageHeader header,
            ChannelWriter<RpcMessage> replyWriter)
        {
            _ = ProcessMessageAsync(payload, header, replyWriter);
        }
    }
}
