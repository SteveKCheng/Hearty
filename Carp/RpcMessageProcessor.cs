using MessagePack;
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Carp
{
    /// <summary>
    /// Accepts incoming non-reply messages for processing
    /// from an RPC connection.
    /// </summary>
    internal abstract class RpcMessageProcessor
    {
        /// <summary>
        /// Accepts an incoming message and performs the desired processing of it.
        /// </summary>
        /// <param name="payload">
        /// The payload of the RPC message.
        /// </param>
        /// <param name="header">
        /// The header of the RPC message containing routing information.
        /// </param>
        /// <param name="connection">The RPC connection
        /// from which the message comes from.  The same
        /// connection may be used to send back reply messages.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token that may be triggered by the RPC client
        /// to interrupt processing of the message.
        /// </param>
        public abstract void ProcessMessage(in ReadOnlySequence<byte> payload,
                                            RpcMessageHeader header,
                                            RpcConnection connection, 
                                            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Invokes an arbitrary delegate in response to a RPC request,
    /// with the necessary serialization/de-serialization of payloads.
    /// </summary>
    /// <typeparam name="TRequest">User-defined type for the request inputs. </typeparam>
    /// <typeparam name="TReply">User-defined type for the reply outputs. </typeparam>
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
                var options = connection.Registry.SerializeOptions;
                var request = MessagePackSerializer.Deserialize<TRequest>(payload, options);

                var replyTask = _func.Invoke(request, connection, cancellationToken);
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
