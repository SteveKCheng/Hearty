using MessagePack;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Enables both sides of a WebSocket connection to make remote procedure calls.
    /// </summary>
    public class WebSocketRpc
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
        /// User-specified functions to invoke to
        /// handle each incoming type of request message.
        /// </summary>
        private readonly Dictionary<ushort, RpcMessageProcessor> _requestDispatch;

        private readonly Task _writePendingMessagesTask;
        private readonly Task _readPendingMessagesTask;

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
        /// <param name="requestRegistry">
        /// Collection of user-specified functions to invoke in response
        /// to incoming requests on the WebSocket connection.  This collection
        /// gets frozen when it is passed to this constructor.
        /// </param>
        public WebSocketRpc(WebSocket webSocket, RpcRequestRegistry requestRegistry)
        {
            _requestDispatch = requestRegistry.Capture();
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

        /// <summary>
        /// Send a request message to the other side of the connection
        /// to invoke a procedure.
        /// </summary>
        /// <typeparam name="TRequest">Type of request inputs to be serialized. </typeparam>
        /// <typeparam name="TReply">Type of reply outputs to be de-serialized when it comes in. </typeparam>
        /// <param name="typeCode">
        /// The user-assigned type code of the request message.
        /// </param>
        /// <param name="message">
        /// Holds any parameters needed for the request.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel the request.
        /// </param>
        /// <returns>
        /// Asynchronous task for the reply outputs.
        /// </returns>
        public async ValueTask<TReply> InvokeRemotelyAsync<TRequest, TReply>(ushort typeCode, TRequest message, CancellationToken cancellationToken)
        {
            var impl = new RequestMessage<TRequest, TReply>(typeCode, message);
            await _channel.Writer.WriteAsync(impl, cancellationToken)
                                 .ConfigureAwait(false);
            return await impl.ReplyTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Drain messages from this client's channel and write them
        /// to the WebSocket connection.
        /// </summary>
        private async Task WritePendingMessagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var channelReader = _channel.Reader;
                uint id = 0;

                while (await channelReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (channelReader.TryRead(out var item))
                    {
                        await WriteMessageAsync(ref id, item, cancellationToken)
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
            bool isReply = IsReplyMessageKind(header.Kind);

            if (isReply)
            {
                RpcMessage? item;
                lock (_pendingReplies)
                {
                    _pendingReplies.Remove(header.Id, out item);
                }

                if (item != null)
                {
                    // FIXME check typeCode against item.TypeCode
                    item.ProcessReplyMessage(payload);
                }
            }
            else
            {
                if (_requestDispatch.TryGetValue(header.TypeCode, out var processor))
                {
                    processor.ProcessMessage(payload, header, _channel.Writer);
                }
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
        /// <param name="currentId">
        /// The sequence number to label the next message with
        /// if it is a request.
        /// </param>
        /// <param name="item">
        /// State object for serializing the message and
        /// de-serializing its reply, if any.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel the sending of the message.
        /// </param>
        private ValueTask WriteMessageAsync(ref uint currentId, 
                                            RpcMessage item, 
                                            CancellationToken cancellationToken = default)
        {
            var writer = _writeBuffers;
            writer.Clear();

            bool isReply = IsReplyMessageKind(item.Kind);

            var id = isReply ? item.ReplyId : unchecked(currentId++);
            var header = new RpcMessageHeader(item.Kind, item.TypeCode, id);

            WriteHeader(writer, header);
            item.PackMessage(writer);

            if (item.Kind == RpcMessageKind.Request)
            {
                lock (_pendingReplies)
                {
                    _pendingReplies.Add(id, item);
                }
            }

            return _webSocket.SendAsync(_writeBuffers.WrittenMemory,
                                        WebSocketMessageType.Binary,
                                        true,
                                        cancellationToken);
        }
    }
}
