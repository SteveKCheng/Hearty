using System;

namespace Hearty.Server;

/// <summary>
/// The header fields for the serialized representation of a promise.
/// </summary>
internal struct PromiseSerializationHeader
{
    public uint FormatVersion;

    public uint TotalLength;

    public PromiseId PromiseId;

    public uint InputLength;

    public uint OutputLength;

    public uint InputSchemaCode;

    public uint OutputSchemaCode;

    public ulong CreationTime;

    public ulong CompletionTime;
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
