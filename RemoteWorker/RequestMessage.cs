using System;
using System.Buffers;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using MessagePack;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Holds the request data before it gets sent over WebSockets,
    /// and tracks the state of the corresponding reply.
    /// </summary>
    /// <typeparam name="TRequest">User-defined type for the request inputs. </typeparam>
    /// <typeparam name="TReply">User-defined type for the reply outputs. </typeparam>
    internal class RequestMessage<TRequest, TReply> : RpcMessage, IValueTaskSource<TReply>
    {
        /// <summary>
        /// The user-defined inputs to the remote procedure call.
        /// </summary>
        public TRequest Body { get; }

        public RequestMessage(ushort typeCode, TRequest body)
            : base(typeCode, RpcMessageKind.Request)
        {
            Body = body;
            _taskSource.RunContinuationsAsynchronously = true;
        }

        #region Implementation of ValueTask for the reply message

        private ManualResetValueTaskSourceCore<TReply> _taskSource;

        /// <summary>
        /// The asynchronous task that completes with the reply
        /// to this request message.
        /// </summary>
        public ValueTask<TReply> ReplyTask => new ValueTask<TReply>(this, _taskSource.Version);

        TReply IValueTaskSource<TReply>.GetResult(short token) => _taskSource.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<TReply>.GetStatus(short token) => _taskSource.GetStatus(token);

        void IValueTaskSource<TReply>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _taskSource.OnCompleted(continuation, state, token, flags);

        #endregion

        public override void PackMessage(IBufferWriter<byte> writer)
        {
            MessagePackSerializer.Serialize(writer, Body, options: null);
        }

        public override void ProcessReplyMessage(in ReadOnlySequence<byte> payload)
        {
            try
            {
                var replyMessage = MessagePackSerializer.Deserialize<TReply>(payload, options: null);
                _taskSource.SetResult(replyMessage);
            }
            catch (Exception e)
            {
                _taskSource.SetException(e);
            }
        }
    }
}