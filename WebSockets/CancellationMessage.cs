using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.WebSockets
{
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
}
