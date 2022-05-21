using System;
using System.Buffers;

namespace Hearty.Carp;

internal sealed class CancellationMessage : RpcMessage
{
    public CancellationMessage(ushort typeCode, uint id, bool isAcknowledgement)
        : base(isAcknowledgement ? RpcMessageKind.AcknowledgedCancellation
                                 : RpcMessageKind.Cancellation,
              typeCode,
              id)
    {
    }

    public override void PackPayload(IBufferWriter<byte> writer)
    {
    }

    public override bool Abort(Exception e) => true;
}
