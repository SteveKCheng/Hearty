using System;
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
        private readonly Dictionary<uint, RpcMessage> _pendingReplies = new();

        /// <summary>
        /// Manages cancellation tokens of received but unfinished requests.
        /// </summary>
        private readonly Dictionary<uint, CancellationSourcePool.Use> _cancellations = new();

        /// <summary>
        /// User-specified functions to invoke to
        /// handle each incoming type of request message.
        /// </summary>
        private readonly Dictionary<ushort, RpcMessageProcessor> _requestDispatch;

        private readonly Task _writePendingMessagesTask;
        private readonly Task _readPendingMessagesTask;

        /// <summary>
        /// ID (sequence number) for sending the next request message;
        /// atomically incremented each time.
        /// </summary>
        private uint _nextRequestId;

        public Task Completion => _writePendingMessagesTask;

        public void RequestCompletion()
        {
            _channel.Writer.TryComplete();
        }

        /// <summary>
        /// Layer on remote procedure call services on top of a WebSocket connection.
        /// </summary>
        /// <param name="webSocket">
        /// An already established WebSocket connection to run the
        /// protocol for the remote procedure calls.
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
            try
            {
                var channelReader = _channel.Reader;

                while (await channelReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (channelReader.TryRead(out var item))
                    {
                        await WriteMessageAsync(item, cancellationToken)
                                .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable,
                                            "WebSocket RPC channel is being closed. ",
                                            cancellationToken)
                                .ConfigureAwait(false);
            }
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

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Binary)
                        IngestMessage(new ReadOnlySequence<byte>(_readBuffers.WrittenMemory));
                }
                finally
                {
                    _readBuffers.Clear();
                }
            }
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
