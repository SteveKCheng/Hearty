using MessagePack;
using System;
using System.Buffers;
using System.Threading.Tasks;

namespace JobBank.WebSockets
{
    internal abstract class RpcMessageProcessor
    {
        public abstract void ProcessMessage(
            in ReadOnlySequence<byte> payload,
            RpcMessageHeader header,
            RpcConnection connection);
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
                                               RpcConnection connection)
        {
            try
            {
                var request = MessagePackSerializer.Deserialize<TRequest>(
                                        payload, options: null);

                var replyTask = _func.Invoke(request, default);
                var reply = await replyTask.ConfigureAwait(false);
                await connection.SendReplyAsync(header.TypeCode, header.Id, reply)
                                .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                await connection.SendExceptionAsync(header.TypeCode, header.Id, e)
                                .ConfigureAwait(false);
            }
        }

        public override void ProcessMessage(
            in ReadOnlySequence<byte> payload, 
            RpcMessageHeader header,
            RpcConnection connection)
        {
            _ = ProcessMessageAsync(payload, header, connection);
        }
    }
}
