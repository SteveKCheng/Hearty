using MessagePack;
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.WebSockets
{
    internal abstract class RpcMessageProcessor
    {
        public abstract void ProcessMessage(
            in ReadOnlySequence<byte> payload,
            RpcMessageHeader header,
            RpcConnection connection, 
            CancellationToken cancellationToken);
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
                                               RpcConnection connection,
                                               CancellationToken cancellationToken)
        {
            try
            {
                var request = MessagePackSerializer.Deserialize<TRequest>(
                                        payload, options: null);

                var replyTask = _func.Invoke(request, cancellationToken);
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
            RpcConnection connection, 
            CancellationToken cancellationToken)
        {
            _ = ProcessMessageAsync(payload, header, connection, cancellationToken);
        }
    }
}
