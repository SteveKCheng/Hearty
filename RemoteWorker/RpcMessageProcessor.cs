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

        private static async Task SendReplyAsync(ValueTask<TReply> replyTask,
                                                 uint id,
                                                 ushort typeCode,
                                                 ChannelWriter<RpcMessage> replyWriter)
        {
            try
            {
                var replyMessage = await replyTask.ConfigureAwait(false);
                var item = new ReplyMessage<TReply>(typeCode, replyMessage, id);
                await replyWriter.WriteAsync(item).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        public override void ProcessMessage(
            in ReadOnlySequence<byte> payload, 
            RpcMessageHeader header,
            ChannelWriter<RpcMessage> replyWriter)
        {
            var request = MessagePackSerializer.Deserialize<TRequest>(
                                    payload, options: null);
            var replyTask = _func.Invoke(request, default);
            _ = SendReplyAsync(replyTask, header.Id, header.TypeCode, replyWriter);
        }
    }
}
