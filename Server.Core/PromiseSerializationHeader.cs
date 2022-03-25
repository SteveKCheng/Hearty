using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Hearty.Server;

/// <summary>
/// The header fields for the serialized representation of a promise.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PromiseSerializationHeader
{
    public uint HeaderLength;

    public uint Reserved;

    public uint InputLength;

    public uint OutputLength;

    public uint InputSchemaCode;

    public uint OutputSchemaCode;

    public PromiseId PromiseId;

    public ulong CreationTime;

    public ulong CompletionTime;

    public static PromiseSerializationHeader Deserialize(ReadOnlySpan<byte> buffer)
    {
        var result = new PromiseSerializationHeader();

        result.HeaderLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer); buffer = buffer.Slice(sizeof(uint));
        result.Reserved = BinaryPrimitives.ReadUInt32LittleEndian(buffer); buffer = buffer.Slice(sizeof(uint));
        result.InputLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer); buffer = buffer.Slice(sizeof(uint));
        result.OutputLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer); buffer = buffer.Slice(sizeof(uint));
        result.InputSchemaCode = BinaryPrimitives.ReadUInt32LittleEndian(buffer); buffer = buffer.Slice(sizeof(uint));
        result.OutputSchemaCode = BinaryPrimitives.ReadUInt32LittleEndian(buffer); buffer = buffer.Slice(sizeof(uint));
        result.PromiseId = new PromiseId(BinaryPrimitives.ReadUInt64LittleEndian(buffer)); buffer = buffer.Slice(sizeof(ulong));

        return result;
    }

    public void Serialize(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, this.HeaderLength); buffer = buffer.Slice(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, this.Reserved); buffer = buffer.Slice(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, this.InputLength); buffer = buffer.Slice(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, this.OutputLength); buffer = buffer.Slice(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, this.InputSchemaCode); buffer = buffer.Slice(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, this.OutputSchemaCode); buffer = buffer.Slice(sizeof(uint));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, this.PromiseId.RawInteger); buffer = buffer.Slice(sizeof(ulong));
    }
}

/// <summary>
/// Basic information about an instance of <see cref="PromiseData" /> 
/// to start decoding its serialization.
/// </summary>
public readonly struct PromiseDataSerializationInfo
{
    /// <summary>
    /// The length of the payload, in bytes, that the derived class of
    /// <see cref="PromiseData" /> serializes.
    /// </summary>
    /// <remarks>
    /// This length must be known upfront.  It is populated
    /// into the header for the serialization of the 
    /// containing promise, and may be consulted to pre-allocate
    /// buffers.
    /// </remarks>
    public uint PayloadLength { get; init; }

    /// <summary>
    /// An internal code consulted upon de-serializing to
    /// instantiate the correct sub-class of <see cref="PromiseData" />.
    /// </summary>
    public uint SchemaCode { get; init; }
}
