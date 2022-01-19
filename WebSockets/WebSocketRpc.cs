﻿using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using JobBank.Utilities;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Enables both sides of a WebSocket connection to make remote procedure calls.
    /// </summary>
    public class WebSocketRpc : RpcConnection
    {
        /// <summary>
        /// WebSocket connection established externally.
        /// </summary>
        private readonly WebSocket _webSocket;

        /// <summary>
        /// Provides in-memory buffers to send outgoing messages.
        /// </summary>
        private readonly ArrayBufferWriter<byte> _writeBuffers;

        /// <summary>
        /// Provides in-memory buffers to receive incoming messages.
        /// </summary>
        private readonly ArrayBufferWriter<byte> _readBuffers;

        /// <summary>
        /// Queues up messages to write to the WebSocket connection.
        /// </summary>
        private readonly Channel<RpcMessage> _channel;

        /// <summary>
        /// Tracks the state of procedure calls made from this client.
        /// </summary>
        /// <remarks>
        /// This object needs to be accessed by the message-writing
        /// task as it writes out request messages over the RPC connection,
        /// and by the message-reading task as it processes reply
        /// messages from the remote end.  Thus this object
        /// must be locked on any access.
        /// </remarks>
        private readonly Dictionary<uint, RpcMessage> _pendingReplies = new();

        /// <summary>
        /// Manages cancellation tokens of received but unfinished requests.
        /// </summary>
        /// <remarks>
        /// This object needs to be accessed by the message-reading task 
        /// as it processes request messages, and by any thread that is
        /// about to enqueue a reply message.  Thus this object
        /// must be locked on any access.
        /// </remarks>
        private readonly Dictionary<uint, CancellationSourcePool.Use> _cancellations = new();

        /// <summary>
        /// User-specified functions to invoke to
        /// handle each incoming type of request message.
        /// </summary>
        private readonly Dictionary<ushort, RpcMessageProcessor> _requestDispatch;

        /// <summary>
        /// Internal task that writes out enqueued messages over
        /// the sending side of the WebSocket connection.
        /// </summary>
        private readonly Task _writePendingMessagesTask;

        /// <summary>
        /// Internal task that reads messages over
        /// the receiving side of the WebSocket connection,
        /// and invokes processing on them.
        /// </summary>
        private readonly Task _readPendingMessagesTask;

        /// <summary>
        /// ID (sequence number) for sending the next request message;
        /// atomically incremented each time.
        /// </summary>
        private uint _nextRequestId;

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
        public async ValueTask WaitForCloseAsync()
        {
            await _writePendingMessagesTask.ConfigureAwait(false);
            await _readPendingMessagesTask.ConfigureAwait(false);
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
        public void Quit() => Terminate(WebSocketCloseStatus.NormalClosure);

        /// <summary>
        /// Request that this RPC connection be shut down, possibly because
        /// of an error.
        /// </summary>
        /// <param name="status">
        /// The status to report as part of WebSocket's close message.
        /// </param>
        private void Terminate(WebSocketCloseStatus status)
        {
            var e = (status != WebSocketCloseStatus.NormalClosure)
                        ? new WebSocketRpcException(status)
                        : null;

            if (_channel.Writer.TryComplete(e))
                _toTerminate = true;
        }

        /// <summary>
        /// Set to true as soon as <see cref="Quit" /> has been called.
        /// </summary>
        /// <remarks>
        /// <see cref="Quit" /> will close <see cref="_channel" />, but
        /// there may be still be messages in the queue, which should
        /// be ignored even though they occur before the closing sentinel
        /// item of the channel.
        /// </remarks>
        private bool _toTerminate;

        /// <summary>
        /// Set to true when the WebSocket connection from this side
        /// should be closed because the remote closed its end.
        /// </summary>
        /// <remarks>
        /// This flag is stored only for the sake of reporting the
        /// proper reason for closing the RPC connection.
        /// It is stored by the message-reading task and 
        /// read by the message-writing task in a way such that
        /// races are harmless.
        /// </remarks>
        private bool _remoteEndHasTerminated;

        /// <summary>
        /// Layer on remote procedure call services on top of a WebSocket connection.
        /// </summary>
        /// <param name="webSocket">
        /// A (new) WebSocket connection established to run the
        /// protocol for the remote procedure calls.  Once passed in,
        /// this instance takes over the WebSocket connection, and
        /// it must not be used elsewhere anymore.
        /// </param>
        /// <param name="registry">
        /// Collection of user-specified functions to invoke in response
        /// to incoming requests on the WebSocket connection.  This collection
        /// gets frozen when it is passed to this constructor.
        /// </param>
        /// <param name="state">The reference that is assigned
        /// to <see cref="State" />.
        /// </param>
        public WebSocketRpc(WebSocket webSocket, RpcRegistry registry, object? state = null)
            : base(registry, state)
        {
            _requestDispatch = registry.Capture();
            _webSocket = webSocket;
            _writeBuffers = new ArrayBufferWriter<byte>(initialCapacity: 4096);
            _readBuffers = new ArrayBufferWriter<byte>(initialCapacity: 4096);
            _channel = Channel.CreateBounded<RpcMessage>(new BoundedChannelOptions(200)
            {
                SingleReader = true
            });

            _writePendingMessagesTask = WritePendingMessagesAsync(CancellationToken.None);
            _readPendingMessagesTask = ReadPendingMessagesAsync(CancellationToken.None);
        }

        protected override sealed ValueTask<uint> GetNextRequestIdAsync()
            => ValueTask.FromResult(Interlocked.Increment(ref _nextRequestId));

        /// <summary>
        /// Drain messages from this client's channel and write them
        /// to the WebSocket connection.
        /// </summary>
        private async Task WritePendingMessagesAsync(CancellationToken cancellationToken)
        {
            var channelReader = _channel.Reader;

            try
            {
                while (!_toTerminate && await channelReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (!_toTerminate && channelReader.TryRead(out var item))
                    {
                        await WriteMessageAsync(item, cancellationToken)
                                .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                // Need to drain _channel first to read status reliably
                DrainPendingMessages();

                WebSocketCloseStatus status;
                if (_channel.Reader.Completion.Exception is AggregateException ae)
                {
                    var e = ae.InnerException as WebSocketRpcException;
                    status = e?.CloseStatus ?? WebSocketCloseStatus.InternalServerError;
                }
                else if (_remoteEndHasTerminated)
                {
                    status = _webSocket.CloseStatus ?? WebSocketCloseStatus.Empty;
                }
                else
                {
                    status = WebSocketCloseStatus.NormalClosure;
                }

                await _webSocket.CloseAsync(status, null, cancellationToken)
                                .ConfigureAwait(false);

                DrainPendingReplies();
            }
        }

        /// <summary>
        /// Drain every message remaining in <see cref="_channel" />,
        /// to be called when it gets closed.
        /// </summary>
        /// <remarks>
        /// This method ensures the asynchronous tasks associated
        /// with any request message will complete, 
        /// with an exception to the effect that the RPC channel
        /// has closed.
        /// </remarks>
        private void DrainPendingMessages()
        {
            var channelReader = _channel.Reader;
            Exception? exception = null; 

            while (channelReader.TryRead(out var item))
            {
                exception ??= new Exception("RPC connection has closed. ");
                item.Abort(exception);
            }
        }

        /// <summary>
        /// Clean up all entries in <see cref="_pendingReplies" />,
        /// when the RPC channel is to close.
        /// </summary>
        /// <remarks>
        /// This method ensures the asynchronous tasks associated
        /// with any request message will complete, 
        /// with an exception to the effect that the RPC channel
        /// has closed.
        /// </remarks>
        private void DrainPendingReplies()
        {
            RpcMessage[] items;
            lock (_pendingReplies)
            {
                int count = _pendingReplies.Count;
                if (count == 0)
                    return;

                items = new RpcMessage[count];
                int i = 0;
                foreach (var (_, item) in _pendingReplies)
                    items[i++] = item;

                _pendingReplies.Clear();
            }

            var exception = new Exception("RPC connection has closed. ");
            foreach (var item in items)
                item.Abort(exception);
        }

        /// <summary>
        /// Read incoming messages from the other end of the WebSocket connection
        /// and process them.
        /// </summary>
        private async Task ReadPendingMessagesAsync(CancellationToken cancellationToken)
        {
            while (_webSocket.CloseStatus == null)
            {
                try
                {
                    ValueWebSocketReceiveResult result;

                    do
                    {
                        var memory = _readBuffers.GetMemory(4096);
                        result = await _webSocket.ReceiveAsync(memory, cancellationToken)
                                                 .ConfigureAwait(false);

                        _readBuffers.Advance(result.Count);
                    } while (!result.EndOfMessage);

                    // If the remote side is initiating termination,
                    // then terminate ourselves too.
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (!_toTerminate)
                        {
                            _remoteEndHasTerminated = true;
                            Quit();
                        }

                        break;
                    }

                    // Do not process incoming messages if we are
                    // already terminating; only drain them from the
                    // WebSocket connection until we see the Close message.
                    if (_toTerminate)
                        continue;

                    if (result.MessageType == WebSocketMessageType.Binary)
                        IngestMessage(new ReadOnlySequence<byte>(_readBuffers.WrittenMemory));
                }
                finally
                {
                    _readBuffers.Clear();
                }
            }

            DrainCancellations();
        }

        /// <summary>
        /// Clean up all entries in <see cref="_cancellations" />,
        /// when the RPC channel is to close.
        /// </summary>
        /// <remarks>
        /// This method cancels all outstanding invocations 
        /// of asynchronous functions from earlier RPC requests.
        /// </remarks>
        private void DrainCancellations()
        {
            CancellationTokenSource?[] sources;
            lock (_cancellations)
            {
                int count = _cancellations.Count;
                if (count == 0)
                    return;

                sources = new CancellationTokenSource?[count];
                int i = 0;
                foreach (var (_, use) in _cancellations)
                    sources[i++] = use.Source;

                _cancellations.Clear();
            }

            foreach (var source in sources)
                source?.Cancel();
        }

        /// <summary>
        /// Read and decode the header bytes introducing an RPC message.
        /// </summary>
        /// <param name="buffer">
        /// Buffer positioned at the start of the RPC message including its header.
        /// On successful return, its position is advanced past the header
        /// to the start of the payload.
        /// </param>
        private static RpcMessageHeader ReadHeader(ref ReadOnlySequence<byte> buffer)
        {
            ulong v;
            var firstSpan = buffer.FirstSpan;
            if (firstSpan.Length >= sizeof(ulong))
            {
                v = BinaryPrimitives.ReadUInt64LittleEndian(firstSpan);
            }
            else
            {
                // Slow path for pathological buffer sizes
                Span<byte> span = stackalloc byte[sizeof(ulong)];
                buffer.Slice(0, sizeof(ulong)).CopyTo(span);
                v = BinaryPrimitives.ReadUInt64LittleEndian(span);
            }

            var h = RpcMessageHeader.Unpack(v);
            buffer = buffer.Slice(sizeof(ulong));
            return h;
        }

        /// <summary>
        /// Write the header bytes introducing an RPC message.
        /// </summary>
        /// <param name="writer">
        /// Writer prepared to start the RPC message.
        /// </param>
        /// <param name="header">
        /// The header information to encode and write.
        /// </param>
        private static void WriteHeader(IBufferWriter<byte> writer, RpcMessageHeader header)
        {
            ulong v = header.Pack();
            var span = writer.GetSpan(sizeof(ulong));
            BinaryPrimitives.WriteUInt64LittleEndian(span, v);
            writer.Advance(sizeof(ulong));
        }

        /// <summary>
        /// De-serialize and process one message after it has been 
        /// read into in-memory buffers.
        /// </summary>
        private void IngestMessage(ReadOnlySequence<byte> payload)
        {
            var header = ReadHeader(ref payload);
            CancellationSourcePool.Use cancellationUse;

            switch (header.Kind)
            {
                case RpcMessageKind.NormalReply:
                case RpcMessageKind.ExceptionalReply:
                    RpcMessage? item;
                    lock (_pendingReplies)
                        _pendingReplies.Remove(header.Id, out item);

                    if (item != null)
                    {
                        // FIXME check typeCode against item.TypeCode
                        bool isException = (header.Kind == RpcMessageKind.ExceptionalReply);
                        item.ProcessReply(payload, isException);
                    }
                    break;

                case RpcMessageKind.Request:
                    if (!_requestDispatch.TryGetValue(header.TypeCode, out var processor))
                        return;

                    cancellationUse = CancellationSourcePool.Rent();
                    try
                    {
                        lock (_cancellations)
                            _cancellations.Add(header.Id, cancellationUse);
                    }
                    catch
                    {
                        cancellationUse.Dispose();
                        throw;
                    }

                    processor.ProcessMessage(payload, header, this, cancellationUse.Token);
                    break;

                case RpcMessageKind.Cancellation:
                    lock (_cancellations)
                        _cancellations.Remove(header.Id, out cancellationUse);

                    cancellationUse.Source?.Cancel();
                    break;

                default:
                    // FIXME report error
                    break;
            }
        }

        /// <summary>
        /// Whether the RPC message is a reply to an earlier request.
        /// </summary>
        private static bool IsReplyMessageKind(RpcMessageKind kind)
            => (kind & RpcMessageKind.NormalReply) != 0;
            
        /// <summary>
        /// Writes one message into the WebSocket connection after
        /// serializing it.
        /// </summary>
        /// <param name="item">
        /// State object for serializing the message and
        /// de-serializing its reply, if any.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel the sending of the message.
        /// </param>
        private ValueTask WriteMessageAsync(RpcMessage item, 
                                            CancellationToken cancellationToken)
        {
            var writer = _writeBuffers;
            writer.Clear();

            WriteHeader(writer, item.Header);
            item.PackPayload(writer);

            if (item.Kind == RpcMessageKind.Request)
            {
                // If the request got cancelled after the user made it
                // but before its message got sent, do not send the
                // message.  If the cancellation message is queued
                // after then this guard would only be a performance
                // optimization, but if it is queued before, due to
                // some multi-thread race, then this guard is critical!
                if (item.IsCancelled)
                    return ValueTask.CompletedTask;

                lock (_pendingReplies)
                    _pendingReplies.Add(item.ReplyId, item);
            }
            else if (item.Kind == RpcMessageKind.Cancellation)
            {
                bool success;
                lock (_pendingReplies)
                    success = _pendingReplies.Remove(item.ReplyId);

                // Do not send cancellation message if it has already
                // been sent, or if the reply has already been received,
                // or if the original request has not even been sent yet.
                if (!success)
                    return ValueTask.CompletedTask;
            }

            return _webSocket.SendAsync(_writeBuffers.WrittenMemory,
                                        WebSocketMessageType.Binary,
                                        true,
                                        cancellationToken);
        }

        private protected override sealed ValueTask<bool> SendMessageAsync(RpcMessage message)
        {
            if (IsReplyMessageKind(message.Kind))
            {
                CancellationSourcePool.Use cancellationUse;
                bool success;
                lock (_cancellations)
                    success = _cancellations.Remove(message.ReplyId, out cancellationUse);

                // Do not reply if already cancelled
                if (!success)
                    return ValueTask.FromResult(true);

                cancellationUse.Dispose();
            }

            return _channel.Writer.TryWriteAsync(message);
        }
    }
}
