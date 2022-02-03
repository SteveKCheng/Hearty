using System;
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
    public abstract class RpcConnection : IDisposable, IAsyncDisposable
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

        /// <summary>
        /// True when the connection started to close or terminate,
        /// possibly because of an error.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This flag is flipped on before any failed replies are
        /// dispatched to outstanding asynchronous function calls,
        /// so they can know that retrying the call on the same
        /// connection is useless.  
        /// </para>
        /// <para>
        /// <see cref="IsClosingStarted" /> becomes true
        /// before clean-up actions are started, and only after
        /// those finish does <see cref="HasClosed" /> become
        /// true.
        /// </para>
        /// </remarks>
        public abstract bool IsClosingStarted { get; }

        /// <summary>
        /// True when the connection has completely closed
        /// and all clean-up actions have occurred.
        /// </summary>
        public bool HasClosed { get; private set; }

        protected void InvokeOnClose(Exception? exception)
        {
            HasClosed = true;
            OnClose?.Invoke(this, new RpcConnectionCloseEventArgs(exception));
        }

        /// <summary>
        /// Request that this RPC connection be gracefully shut down.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Outstanding asynchronous function calls will throw exceptions
        /// indicating that the RPC connection has been closed.
        /// </para>
        /// <para>
        /// This method only requests the connection be torn down.
        /// Closing the connection requires a handshake which will
        /// occur asynchronously; use <see cref="WaitForCloseAsync" />
        /// to wait for that handshake to complete, before
        /// disposing this instance.
        /// </para>
        /// </remarks>
        public abstract void Quit();

        /// <summary>
        /// Wait for the RPC connection to be torn down.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method waits gracefully if teardown was
        /// initiated by a call to <see cref="Quit" />
        /// either on this instance or on the remote end
        /// of the connection.
        /// </para>
        /// <para>
        /// This method itself does not initiate termination.
        /// </para>
        /// </remarks>
        /// <returns>
        /// Asynchronous task that completes when the connection is closed.
        /// If the connection terminated because of an error, that error
        /// will be thrown as an exception.
        /// </returns>
        public abstract Task WaitForCloseAsync();

        /// <summary>
        /// Abruptly and ungracefully terminate the connection 
        /// unless it is already terminated.
        /// </summary>
        public abstract void Abort();

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

        /// <summary>
        /// "Dispose pattern" to implement <see cref="IDisposable.Dispose" />.
        /// </summary>
        /// <param name="isDisposing">
        /// True if explicitly disposing; false when being run from 
        /// a finalizer.
        /// </param>
        /// <remarks>
        /// The default implementation synchronously waits on the
        /// asynchronous task returned by <see cref="DisposeAsync" />.
        /// </remarks>
        protected virtual void DisposeImpl(bool isDisposing)
        {
            if (isDisposing)
                DisposeAsync().AsTask().Wait();
        }

        /// <summary>
        /// Terminates the connection and waits for it to close,
        /// synchronously.
        /// </summary>
        public void Dispose()
        {
            DisposeImpl(isDisposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Terminates the connection and waits for it to close,
        /// asynchronously.
        /// </summary>
        public abstract ValueTask DisposeAsync();
    }
}
