using FASTER.core;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hearty.Server.FasterKV;

[StructLayout(LayoutKind.Explicit)]
internal struct PromiseBlob
{
    [FieldOffset(0)]
    private PromiseSerializationHeader _header;

    internal const byte Flag_HasPayload = 1;

    public PromiseBlob(in PromiseSerializationHeader header)
    {
        _header = header;
    }

    public int TotalLength => (int)_header.TotalLength;

    private readonly bool HasPayload => (_header.BlobStatus & Flag_HasPayload) != 0;

    public unsafe void CopyWithPayloadTo(void* destination)
    {
        fixed (PromiseBlob* sourcePtr = &this)
        {
            int length = HasPayload ? TotalLength : Unsafe.SizeOf<PromiseBlob>();
            var sourceSpan = new Span<byte>(sourcePtr, length);
            var destinationSpan = new Span<byte>(destination, length);
            sourceSpan.CopyTo(destinationSpan);
        }
    }

    public unsafe void CopyWithPayloadTo(ref PromiseBlob destination)
    {
        if (HasPayload)
        {
            fixed (PromiseBlob* sourcePtr = &this)
            fixed (PromiseBlob* destinationPtr = &destination)
            {
                var sourceSpan = new Span<byte>(sourcePtr, TotalLength);
                var destinationSpan = new Span<byte>(destinationPtr, TotalLength);
                sourceSpan.CopyTo(destinationSpan);
            }
        }
        else
        {
            destination = this;
        }
    }

    public unsafe void StoreSerialization(in PromiseSerializationInfo info)
    {
        Debug.Assert(HasPayload && TotalLength >= info.TotalLength);

        fixed (PromiseBlob* ptr = &this)
        {
            var buffer = new Span<byte>(ptr, TotalLength);
            info.Serialize(buffer);
        }
    }
}

internal sealed class PromiseBlobVarLenStruct : IVariableLengthStruct<PromiseBlob>
{
    public int GetInitialLength() => Unsafe.SizeOf<PromiseBlob>();

    public int GetLength(ref PromiseBlob t) => t.TotalLength;

    public unsafe void Serialize(ref PromiseBlob source, void* destination)
        => source.CopyWithPayloadTo(destination);

    public unsafe ref PromiseBlob AsRef(void* source) => ref Unsafe.AsRef<PromiseBlob>(source);

    public unsafe void Initialize(void* source, void* dest) 
    {
        int length = checked((int)((byte*)dest - (byte*)source));
        Debug.Assert(length >= Unsafe.SizeOf<PromiseBlob>());

        ref var blob = ref Unsafe.AsRef<PromiseSerializationHeader>(source);
        blob = PromiseSerializationHeader.AllocateForBlob(
                length, PromiseBlob.Flag_HasPayload);
    }
}
