using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.WebSockets
{
    internal sealed class CancellationMessage : RpcMessage
    {
        public CancellationMessage(ushort typeCode, uint id)
            : base(RpcMessageKind.Cancellation, typeCode, id)
        {
        }

        public override void PackPayload(IBufferWriter<byte> writer)
        {
        }

        public override void ProcessReply(in ReadOnlySequence<byte> payload, bool isException)
            => throw new InvalidOperationException();
    }
}
