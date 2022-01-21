using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Arguments for the <see cref="RpcConnection.OnClose" /> event.
    /// </summary>
    public readonly struct RpcConnectionCloseEventArgs
    {
        /// <summary>
        /// Exceptional condition that caused the connection to close, if any.
        /// </summary>
        /// <remarks>
        /// Graceful closing of the connection initiated from 
        /// either side is not considered an exceptional condition.  
        /// In that case this property reports null.
        /// </remarks>
        public Exception? Exception { get; }

        public RpcConnectionCloseEventArgs(Exception? exception)
            => Exception = exception;
    }

    /// <summary>
    /// An abstract connection capable of bi-directional remote procedure
    /// call as implemented by this library.
    /// </summary>
    public abstract class RpcConnection
    {
        /// <summary>
        /// Directs how payloads and messages on this RPC connection 
        /// are processed.
        /// </summary>
        public RpcRegistry Registry { get; }

        /// <summary>
        /// Reference to an arbitrary object that can be associated
        /// to this connection.
        /// </summary>
        /// <remarks>
        /// This property is provided so that the application can
        /// look up and store application-specific information
        /// for this connection.
        /// </remarks>
        public object? State { get; }

        /// <summary>
        /// Event that fires when the connection closes for any reason.
        /// </summary>
        /// <remarks>
        /// <see cref="HasClosed" /> is guaranteed to be true 
        /// when the event fires.
        /// </remarks>
        public event EventHandler<RpcConnectionCloseEventArgs>? OnClose;

        protected void InvokeOnClose(Exception? exception)
        {
            OnClose?.Invoke(this, new RpcConnectionCloseEventArgs(exception));
        }

        internal ValueTask<bool> SendReplyAsync<TReply>(ushort typeCode, uint id, TReply reply)
            => SendMessageAsync(new ReplyMessage<TReply>(typeCode, id, Registry, reply));

        internal ValueTask<bool> SendExceptionAsync(ushort typeCode, uint id, Exception e)
            => SendMessageAsync(new ExceptionMessage(typeCode, id, Registry, e));

        internal ValueTask<bool> SendCancellationAsync(ushort typeCode, uint id)
            => SendMessageAsync(new CancellationMessage(typeCode, id, isAcknowledgement: false));

        internal async void AcknowledgeCancellation(ushort typeCode, uint id)
        {
            await SendMessageAsync(new CancellationMessage(typeCode,
                                                           id,
                                                           isAcknowledgement: true))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Get the ID that is to be assigned to the next request message.
        /// </summary>
        /// <returns>
        /// The ID, which may be provided asynchronously to
        /// effect flow control.
        /// </returns>
        protected abstract ValueTask<uint> GetNextRequestIdAsync();

        /// <summary>
        /// Send a request message to the other side of the connection
        /// to invoke a procedure.
        /// </summary>
        /// <typeparam name="TRequest">Type of request inputs to be serialized. </typeparam>
        /// <typeparam name="TReply">Type of reply outputs to be de-serialized when it comes in. </typeparam>
        /// <param name="typeCode">
        /// The user-assigned type code of the request message.
        /// </param>
        /// <param name="request">
        /// Holds any parameters needed for the request.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel the request.
        /// </param>
        /// <returns>
        /// Asynchronous task for the reply outputs.
        /// </returns>
        public async ValueTask<TReply> InvokeRemotelyAsync<TRequest, TReply>(
            ushort typeCode,
            TRequest request,
            CancellationToken cancellationToken)
        {
            var id = await GetNextRequestIdAsync().ConfigureAwait(false);
            var item = new RequestMessage<TRequest, TReply>(typeCode,
                                                            id,
                                                            request,
                                                            this,
                                                            cancellationToken);

            if (!await SendMessageAsync(item).ConfigureAwait(false))
                throw new Exception("The RPC connection is already closed. ");

            return await item.ReplyTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Send (or enqueue to send) a message on the RPC channel.
        /// </summary>
        /// <param name="message">The desired message. </param>
        /// <returns>
        /// An asynchronous task that completes with true when
        /// the item has been successfully sent, or false
        /// if the RPC channel has already been closed.
        /// </returns>
        private protected abstract ValueTask<bool> SendMessageAsync(RpcMessage message);

        /// <summary>
        /// Sets basic/common information about the RPC connection
        /// on construction.
        /// </summary>
        /// <param name="registry">The reference that is stored
        /// in <see cref="Registry" />.
        /// </param>
        /// <param name="state">The reference that is assigned
        /// to <see cref="State" />.
        /// </param>
        protected RpcConnection(RpcRegistry registry, object? state)
        {
            Registry = registry;
            State = state;
        }
    }
}
