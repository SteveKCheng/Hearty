using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hearty.Server;

public partial class Promise
{
    /// <summary>
    /// Get the header to write when this promise is to be serialized.
    /// </summary>
    /// <remarks>
    /// The header provides the length of the serialized representation
    /// which is often required for pre-allocating buffers.
    /// </remarks>
    /// <param name="info">
    /// Set to the prepared information and state to serialize
    /// this promise, when this method returns true.
    /// This preparation is needed to capture the current state
    /// of the object as it may see concurrent updates; also
    /// if the serialization has variable length, it may need
    /// to be serialized to temporary buffers first.
    /// </param>
    /// <returns>
    /// True if this promise in its current state supports serialization,
    /// false otherwise.
    /// </returns>
    public bool TryPrepareSerialization(out PromiseSerializationInfo info)
    {
        var input = RequestOutput;
        var output = ResultOutput;

        PromiseDataSerializationInfo inputInfo = default;
        PromiseDataSerializationInfo outputInfo = default;

        if (input is not null && !input.TryPrepareSerialization(out inputInfo))
        {
            info = default;
            return false;
        }

        if (output is not null && !output.TryPrepareSerialization(out outputInfo))
        {
            info = default;
            return false;
        }

        info = new PromiseSerializationInfo
        {
            Header = new PromiseSerializationHeader
            {
                HeaderLength = PromiseSerializationHeader.Size,
                InputSchemaCode = input is not null ? inputInfo.SchemaCode : (ushort)0,
                OutputSchemaCode = output is not null ? outputInfo.SchemaCode : (ushort)0,
                Reserved = 0,
                InputLength = input is not null ? inputInfo.PayloadLength : 0,
                OutputLength = output is not null ? outputInfo.PayloadLength : 0,
                Id = Id
            },
            Input = inputInfo,
            Output = outputInfo
        };

        return true;
    }

    /// <summary>
    /// Re-materialize a promise object from what 
    /// <see cref="PromiseSerializationInfo.Serialize" />
    /// has written.
    /// </summary>
    /// <param name="schemas">
    /// Mapping of schema codes required to instantiate the correct
    /// derived classes of <see cref="PromiseData" />.
    /// </param>
    /// <param name="data">
    /// The serialized bytes of the promise.
    /// </param>
    /// <returns>
    /// The de-serialized promise object.
    /// </returns>
    public static Promise Deserialize(PromiseDataSchemas schemas,
                                      ReadOnlyMemory<byte> data)
    {
        var header = PromiseSerializationHeader.ReadFrom(data.Span);
        data = data[header.HeaderLength..];

        PromiseData? inputData = null;
        PromiseData? outputData = null;

        if (header.InputSchemaCode != 0)
        {
            var deserializer = schemas[header.InputSchemaCode];
            inputData = deserializer.Invoke(data[0 .. (int)header.InputLength].Span);
            data = data[(int)header.InputLength..];
        }

        if (header.OutputSchemaCode != 0)
        {
            var deserializer = schemas[header.OutputSchemaCode];
            outputData = deserializer.Invoke(data[0..(int)header.OutputLength].Span);
        }

        return new Promise(DateTime.UtcNow, header.Id, inputData, outputData);
    }
}

/// <summary>
/// Prepared state for serializing a promise, opaque to clients.
/// </summary>
public struct PromiseSerializationInfo
{
    internal PromiseSerializationHeader Header;

    internal PromiseDataSerializationInfo Input;

    internal PromiseDataSerializationInfo Output;

    /// <summary>
    /// The total length, in bytes, of the serialization of the promise.
    /// </summary>
    public long TotalLength => Header.TotalLength;

    /// <summary>
    /// Serialize into a buffer allocated by the caller.
    /// </summary>
    /// <remarks>
    /// This method is used to persist the promise outside
    /// of .NET's GC-managed memory.
    /// </remarks>
    /// <param name="buffer">
    /// The buffer to write to.  This buffer is sized
    /// to <see cref="TotalLength" />, and this method
    /// shall write exactly that many bytes.
    /// </param>
    public void Serialize(Span<byte> buffer)
    {
        try
        {
            var headerLength = (int)Header.HeaderLength;
            Header.WriteTo(buffer[0..headerLength]);
            buffer = buffer[headerLength..];

            if (Header.InputLength != 0)
            {
                Input.Serializer.Invoke(Input, buffer[0..(int)Header.InputLength]);
                buffer = buffer[(int)Header.InputLength..];
            }

            if (Header.OutputLength != 0)
            {
                Output.Serializer.Invoke(Output, buffer[0..(int)Header.OutputLength]);
            }
        }
        finally
        {
            if (Input.State is not null)
                Input.StateDisposal?.Invoke(Input.State);

            if (Output.State is not null)
                Output.StateDisposal?.Invoke(Output.State);
        }
    }
}

/// <summary>
/// The header fields for the serialized representation of a promise.
/// </summary>
/// <remarks>
/// <para>
/// As serialization of promises are used to store them in
/// in-process databases, serialization should be compact
/// and quick to read and write.
/// </para>
/// <para>
/// To that end, header fields are stored at fixed offsets,
/// and this structure can "just" be read off a block of bytes.
/// Fortunately, the header fields do not need to be variable-length.
/// </para>
/// <para>
/// We ignore the aspect of endian conversion as Hearty, so far,
/// is expected only to run on little-endian platforms.
/// </para>
/// <para>
/// Clients of this library should not rely on these implementation 
/// details: for this reason, the fields in this structure are not
/// public.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct PromiseSerializationHeader
{
    /// <summary>
    /// The total length, in bytes, of the serialization of the promise.
    /// </summary>
    public long TotalLength => (long)HeaderLength + InputLength + OutputLength;

    /// <summary>
    /// The length in bytes of this header.  
    /// </summary>
    /// <remarks>
    /// This field serves as a version identifier as well,
    /// for old data.  New fields added at the end will necessarily
    /// increase the length of the header.
    /// </remarks>
    internal ushort HeaderLength;

    internal ushort InputSchemaCode;

    internal ushort OutputSchemaCode;

    internal ushort Reserved;

    /// <summary>
    /// The ID of the promise being stored.
    /// </summary>
    internal PromiseId Id;

    /// <summary>
    /// The length in bytes of the serialization of the
    /// input <see cref="PromiseData" />.  
    /// </summary>
    /// <remarks>
    /// <para>
    /// This field is zero if no completed input has been stored
    /// in the <see cref="Promise" /> object.  Otherwise it
    /// is populated from <see cref="PromiseDataSerializationInfo.PayloadLength" />.
    /// </para>
    /// </remarks>
    internal int InputLength;

    /// <summary>
    /// The length in bytes of the serialization of the
    /// output <see cref="PromiseData" />.  
    /// </summary>
    /// <remarks>
    /// <para>
    /// This field is zero if no completed output has been stored
    /// in the <see cref="Promise" /> object.  Otherwise it
    /// is populated from <see cref="PromiseDataSerializationInfo.PayloadLength" />.
    /// </para>
    /// </remarks>
    internal int OutputLength;

    internal ulong CreationTime;

    internal ulong CompletionTime;

    /// <summary>
    /// Read an instance of this structure from a buffer of bytes.
    /// </summary>
    internal static PromiseSerializationHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (!MemoryMarshal.TryRead(buffer, out PromiseSerializationHeader header))
            ThrowExceptionForTooSmallBuffer();
        return header;
    }

    /// <summary>
    /// The size of this structure in bytes.
    /// </summary>
    internal static ushort Size => (ushort)Unsafe.SizeOf<PromiseSerializationHeader>();

    /// <summary>
    /// Write this instance into a buffer of bytes so that it
    /// can be read again by <see cref="ReadFrom" />.
    /// </summary>
    internal readonly void WriteTo(Span<byte> buffer)
    {
        if (!MemoryMarshal.TryWrite(buffer, ref Unsafe.AsRef(this)))
            ThrowExceptionForTooSmallBuffer();
    }

    private static void ThrowExceptionForTooSmallBuffer()
    {
        throw new ArgumentException(
                message: "The supplied buffer is too small " +
                         "for PromiseSerializationHeader. ",
                paramName: "buffer");
    }
}
