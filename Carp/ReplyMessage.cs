using MessagePack;
using System;
using System.Buffers;

namespace Hearty.Carp;

/// <summary>
/// Holds a reply before it gets sent over WebSockets.
/// </summary>
/// <typeparam name="TReply">User-defined type for the reply outputs. </typeparam>
internal sealed class ReplyMessage<TReply> : RpcMessage
{
    public ReplyMessage(ushort typeCode, uint replyId, RpcRegistry registry, TReply body)
        : base(RpcMessageKind.NormalReply, typeCode, replyId)
    {
        _serializeOptions = registry.SerializeOptions;
        Body = body;
    }

    public TReply Body { get; }

    private readonly MessagePackSerializerOptions _serializeOptions;

    public override void PackPayload(IBufferWriter<byte> writer)
    {
        MessagePackSerializer.Serialize(writer, Body, _serializeOptions);
    }
}
