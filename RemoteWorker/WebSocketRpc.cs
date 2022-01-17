using MessagePack;
using System;
using System.Buffers;
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
        private readonly Dictionary<short, (RequestMessageProcessor Processor, object State)>
            _requestDispatch;

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
        public async ValueTask<TReply> InvokeRemotelyAsync<TRequest, TReply>(short typeCode, TRequest message, CancellationToken cancellationToken)
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
        /// De-serialize and process one message after it has been 
        /// read into in-memory buffers.
        /// </summary>
        private void IngestMessage(in ReadOnlySequence<byte> message)
        {
            var reader = new MessagePackReader(message);

            uint id = reader.ReadUInt32();
            ushort messageCode = reader.ReadUInt16();
            bool isReply = (messageCode & 0x8000) != 0;
            short typeCode = (short)(messageCode & 0x7FFF);

            if (isReply)
            {
                RpcMessage? item;
                lock (_pendingReplies)
                {
                    _pendingReplies.Remove(id, out item);
                }

                if (item != null)
                {
                    // FIXME check typeCode against item.TypeCode
                    item.ProcessReplyMessage(ref reader, typeCode);
                }
            }
            else
            {
                if (_requestDispatch.TryGetValue(typeCode, out var dispatch))
                {
                    dispatch.Processor(ref reader, id, typeCode, dispatch.State, _channel.Writer);
                }
            }
        }

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
            _writeBuffers.Clear();
            var writer = new MessagePackWriter(_writeBuffers);

            var id = item.IsReply ? item.ReplyMessageId : unchecked(currentId++);
            var messageCode = ((ushort)item.TypeCode) | (item.IsReply ? (ushort)0x8000 : 0);

            writer.Write(id);
            writer.Write(messageCode);

            item.PackMessage(ref writer, id);

            writer.Flush();

            if (!item.IsReply)
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