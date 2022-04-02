using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hearty.Server;

public sealed partial class Promise
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
    /// <para>
    /// True if this promise in its current state supports serialization,
    /// false otherwise.
    /// </para>
    /// <para>
    /// This promise may not support serialization if the data objects
    /// it contains do not support it, or if at the moment, some data 
    /// is not in a complete state.  Storage providers are then required 
    /// to hold this promise as a .NET object only.
    /// </para>
    /// </returns>
    public bool TryPrepareSerialization(out PromiseSerializationInfo info)
    {
        var input = RequestOutput;
        var output = ResultOutput;

        PromiseDataSerializationInfo inputInfo = default;
        PromiseDataSerializationInfo outputInfo = default;

        if (output?.TryPrepareSerialization(out outputInfo) == false)
        {
            info = default;
            return false;
        }

        if (input?.TryPrepareSerialization(out inputInfo) == false)
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
    /// <param name="fixtures">
    /// Dependencies needed to revive the promise object,
    /// including the data schema registry.
    /// </param>
    /// <param name="data">
    /// The serialized bytes of the promise.
    /// </param>
    /// <returns>
    /// The de-serialized promise object.
    /// </returns>
    public static Promise Deserialize(IPromiseDataFixtures fixtures,
                                      ReadOnlySpan<byte> data)
    {
        var schemas = fixtures.Schemas;

        PromiseData? inputData = null;
        PromiseData? outputData = null;
        PromiseSerializationHeader header;

        try
        {
            header = PromiseSerializationHeader.ReadFrom(data);
            data = data[header.HeaderLength..];

            if (header.InputSchemaCode != 0)
            {
                var deserializer = schemas[header.InputSchemaCode];
                inputData = deserializer.Invoke(fixtures,
                                                data[0..(int)header.InputLength]);
                data = data[(int)header.InputLength..];
            }

            if (header.OutputSchemaCode != 0)
            {
                var deserializer = schemas[header.OutputSchemaCode];
                outputData = deserializer.Invoke(fixtures,
                                                 data[0..(int)header.OutputLength]);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                "De-serialization of promise data failed.  The data may be corrupt. ",
                innerException: e);
        }

        return new Promise(fixtures.Logger,
                           DateTime.UtcNow,
                           header.Id,
                           inputData,
                           outputData);
    }
}

/// <summary>
/// Prepared state for serializing a promise, opaque to clients.
/// </summary>
public struct PromiseSerializationInfo
{
    /// <summary>
    /// The header in the serialized representation of the promise.
    /// </summary>
    public PromiseSerializationHeader Header { get; internal set; }

    internal PromiseDataSerializationInfo Input;

    internal PromiseDataSerializationInfo Output;

    /// <summary>
    /// The total length, in bytes, of the serialization of the promise.
    /// </summary>
    public readonly long TotalLength => Header.TotalLength;

    /// <summary>
    /// Serialize into a buffer allocated by the caller.
    /// </summary>
    /// <remarks>
    /// This method is used to persist the promise outside
    /// of .NET's GC-managed memory.
    /// </remarks>
    /// <param name="buffer">
    /// The buffer to write to.  This buffer must be sized
    /// at least <see cref="TotalLength" />, and this method
    /// shall write exactly that many bytes.
    /// </param>
    public readonly void Serialize(Span<byte> buffer)
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
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PromiseSerializationHeader
{
    /// <summary>
    /// The total length, in bytes, of the serialization of the promise.
    /// </summary>
    /// <remarks>
    /// This property reports the allocated size of the blob
    /// if this header comes from <see cref="AllocateForBlob(int)" />.
    /// </remarks>
    public readonly long TotalLength => (long)HeaderLength + InputLength + OutputLength;

    /// <summary>
    /// The length in bytes of this header.  
    /// </summary>
    /// <remarks>
    /// This field serves as a version identifier as well,
    /// for old data.  New fields added at the end will necessarily
    /// increase the length of the header.
    /// </remarks>
    internal ushort HeaderLength;

    /// <summary>
    /// Schema code for the serialization of the input data.
    /// </summary>
    internal ushort InputSchemaCode;

    /// <summary>
    /// Schema code for the serialization of the output data.
    /// </summary>
    internal ushort OutputSchemaCode;

    /// <summary>
    /// Schema code for the serialization of the metadata.
    /// </summary>
    /// <remarks>
    /// Reserved; not used as currently promise objects do not store metadata.
    /// </remarks>
    internal ushort MetadataSchemaCode;

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
    /// <para>
    /// This field is also used for the dummy header that comes
    /// out of <see cref="AllocateForBlob" />.  For such a dummy
    /// header, <see cref="HeaderLength" /> is set to zero 
    /// (indicating the data is invalid), but the allocated size
    /// is set in this field so that <see cref="TotalLength" />
    /// returns the allocated size.
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

    /// <summary>
    /// The length in bytes of the serialization of the metadata
    /// </summary>
    /// <remarks>
    /// Reserved; not used as currently promise objects do not store metadata.
    /// </remarks>
    internal int MetadataLength;

    /// <summary>
    /// Indicates if the input data is compressed and by what format.
    /// </summary>
    /// <remarks>
    /// Reserved; currently no compression is implemented.
    /// </remarks>
    internal byte InputCompression;

    /// <summary>
    /// Indicates if the output data is compressed and by what format.
    /// </summary>
    /// <remarks>
    /// Reserved; currently no compression is implemented.
    /// </remarks>
    internal byte OutputCompression;

    /// <summary>
    /// Indicates if the metadata is compressed and by what format.
    /// </summary>
    /// <remarks>
    /// Reserved; currently no compression is implemented.
    /// </remarks>
    internal byte MetadataCompression;

    /// <summary>
    /// Reserved for flags that might be stored about the promise.
    /// </summary>
    /// <remarks>
    /// Currently there are no flags.  This field is set to zero.
    /// </remarks>
    internal byte Flags;

    internal ulong CreationTime;

    internal ulong CompletionTime;

    /// <summary>
    /// Read an instance of this structure from a buffer of bytes.
    /// </summary>
    internal static PromiseSerializationHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (!MemoryMarshal.TryRead(buffer, out PromiseSerializationHeader header))
            ThrowExceptionForTooSmallBuffer();

        if (header.HeaderLength < Size)
            throw new InvalidDataException("Header from promise serialization has an invalid length.  It is corrupted. ");

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

    /// <summary>
    /// Create a dummy header (for a database etc.)
    /// indicating that space has been allocated for the serialized data 
    /// but that data has not yet been produced.
    /// </summary>
    /// <param name="length">
    /// The length, in bytes, to set for 
    /// <see cref="PromiseSerializationHeader.TotalLength" />.
    /// </param>
    /// <returns>
    /// A dummy header that has its fields set as indicated by the
    /// arguments.
    /// </returns>
    public static PromiseSerializationHeader AllocateForBlob(int length)
    {
        if (length < Size)
        {
            throw new InvalidOperationException(
                "Attempt to initialize a promise blob onto a memory buffer " +
                "that is sized too small. ");
        }

        return new PromiseSerializationHeader
        {
            InputLength = length
        };
    }
}
