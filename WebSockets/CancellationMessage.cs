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
            : base(typeCode, RpcMessageKind.Cancellation, id)
        {
        }

        public override void PackMessage(IBufferWriter<byte> writer)
        {
        }

        public override void ProcessReplyMessage(in ReadOnlySequence<byte> payload, bool isException)
            => throw new InvalidOperationException();
    }
}
