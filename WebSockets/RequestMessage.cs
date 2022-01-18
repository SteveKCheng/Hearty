using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
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
    internal sealed class RequestMessage<TRequest, TReply> : RpcMessage, IValueTaskSource<TReply>
    {
        /// <summary>
        /// The user-defined inputs to the remote procedure call.
        /// </summary>
        public TRequest Body { get; }

        public RequestMessage(ushort typeCode, 
                              uint id,
                              TRequest body, 
                              RpcConnection connection,
                              CancellationToken cancellationToken)
            : base(typeCode, RpcMessageKind.Request, id)
        {
            Body = body;
            _taskSource.RunContinuationsAsynchronously = true;
            _connection = connection;

            if (cancellationToken.CanBeCanceled)
            {
                _cancellationToken = cancellationToken;
                _cancelRegistration = cancellationToken.UnsafeRegister(
                    static s => Unsafe.As<RequestMessage<TRequest, TReply>>(s!)
                                      .PropagateCancellation(),
                    this);
            }
        }

        #region Cancellation

        private async void PropagateCancellation()
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException e)
            {
                TrySetException(e);
                await _connection.SendCancellationAsync(TypeCode, ReplyId)
                                 .ConfigureAwait(false);
            }
        }

        private readonly CancellationTokenRegistration _cancelRegistration;
        private readonly CancellationToken _cancellationToken;

        private readonly RpcConnection _connection;

        #endregion

        #region Implementation of ValueTask for the reply message

        private ManualResetValueTaskSourceCore<TReply> _taskSource;

        private bool TaskCompleted => 
            _taskSource.GetStatus(_taskSource.Version) != ValueTaskSourceStatus.Pending;

        /// <summary>
        /// The asynchronous task that completes with the reply
        /// to this request message.
        /// </summary>
        public ValueTask<TReply> ReplyTask => new ValueTask<TReply>(this, _taskSource.Version);

        TReply IValueTaskSource<TReply>.GetResult(short token) => _taskSource.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<TReply>.GetStatus(short token) => _taskSource.GetStatus(token);

        void IValueTaskSource<TReply>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _taskSource.OnCompleted(continuation, state, token, flags);

        private bool TrySetException(Exception e)
        {
            lock (this)
            {
                if (TaskCompleted)
                    return false;

                _taskSource.SetException(e);
                _cancelRegistration.Dispose();
                return true;
            }
        }

        private bool TrySetResult(TReply result)
        {
            lock (this)
            {
                if (TaskCompleted)
                    return false;

                _taskSource.SetResult(result);
                _cancelRegistration.Dispose();
                return true;
            }
        }

        #endregion

        public override void PackMessage(IBufferWriter<byte> writer)
        {
            MessagePackSerializer.Serialize(writer, Body, options: null);
        }

        public override void ProcessReplyMessage(in ReadOnlySequence<byte> payload, bool isException)
        {
            try
            {
                if (!isException)
                {
                    var reply = MessagePackSerializer.Deserialize<TReply>(payload, options: null);
                    TrySetResult(reply);
                }
                else
                {
                    var body = MessagePackSerializer.Deserialize<ExceptionMessagePayload>(payload, options: null);
                    var exception = new Exception(body.Description);
                    TrySetException(exception);
                }
            }
            catch (Exception e)
            {
                TrySetException(e);
            }
        }
    }
}