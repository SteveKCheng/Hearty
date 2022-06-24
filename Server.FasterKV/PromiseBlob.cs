using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hearty.Server.FasterKV;

/// <summary>
/// Represents a promise as a serialized blob in a memory buffer
/// (which may be loaded from external storage).
/// </summary>
/// <remarks>
/// <para>
/// This is a "variable-length" structure.  Of course C# and .NET
/// supports no such thing.  This struct type holds only the 
/// fixed-length header, and the variable-length payload is assumed 
/// to immediately follow when the blob is loaded to memory.
/// </para>
/// <para>
/// Of course the technique is "unsafe": even in C/C++ it is not
/// natively supported by the language.  But FASTER KV is designed
/// to take such variable-length structures.  This type should
/// always be passed by reference; passing by value would lose
/// the variable-length payload.
/// </para>
/// <para>
/// Because variable-length memory must be allocated to hold the
/// blob, this type cannot be constructed directly (with normal C# syntax).
/// It must be initialized with <see cref="Initialize(Span{byte})" />.
/// </para>
/// <para>
/// Be careful to never copy instances to a local variable or
/// a member variable!  The variable-length payload will get chopped
/// off, and this type has no way to detect that, so instance methods
/// will likely go off and corrupt the contents of adjacent memory!
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit)]
internal struct PromiseBlob
{
    /// <summary>
    /// The serialization header that is always present at the beginning
    /// of the blob's storage area.
    /// </summary>
    [FieldOffset(0)]
    private PromiseSerializationHeader _header;

    /// <summary>
    /// The minimum allocation size of a buffer that holds
    /// a promise blob.
    /// </summary>
    private static int BaseSize => Unsafe.SizeOf<PromiseBlob>();

    /// <summary>
    /// The total length in bytes of the blob, inclusive of the header
    /// and the variable-length payload.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <para>
    /// This instance is not initialized properly.  
    /// </para>
    /// <para>
    /// Of course, this condition cannot be detected reliably if this 
    /// instance has been re-interpreted off an uninitialized buffer.  
    /// But this exception is guaranteed to trigger if this instance
    /// is (improperly) default-initialized (using normal C# syntax).
    /// </para>
    /// </exception>
    public readonly int TotalLength
    {
        get
        {
            int length = (int)_header.TotalLength;
            if (length < Unsafe.SizeOf<PromiseBlob>())
                ThrowForInvalidInstance();
            return length;
        }
    }

    private static void ThrowForInvalidInstance()
    {
        throw new InvalidOperationException(
            "An operation was attempted on an uninitialized " +
            "or corrupt blob for a promise. ");
    }

    /// <summary>
    /// Copy the contents of this blob to another memory location.
    /// </summary>
    /// <param name="destination">
    /// Pointer to the start of the memory location to copy to.
    /// There must be <see cref="TotalLength" /> bytes available
    /// to write here.
    /// </param>
    public readonly unsafe void CopyTo(void* destination)
    {
        int length = TotalLength;

        fixed (void* sourcePtr = &this)
        {
            var sourceSpan = new Span<byte>(sourcePtr, length);
            var destinationSpan = new Span<byte>(destination, length);
            sourceSpan.CopyTo(destinationSpan);
        }
    }

    /// <summary>
    /// Copy the contents of this blob into another appropriately
    /// sized blob.
    /// </summary>
    /// <param name="destination">
    /// The blob to copy into.  Its existing contents will be 
    /// overwritten.
    /// </param>
    public readonly unsafe void CopyTo(ref PromiseBlob destination)
    {
        int length = TotalLength;
        if (destination.TotalLength < length)
        {
            throw new InvalidOperationException(
                "Attempt to copy a promise blob's contents to " +
                "another blob that is sized too small. ");
        }

        fixed (void* sourcePtr = &this)
        fixed (void* destinationPtr = &destination)
        {
            var sourceSpan = new Span<byte>(sourcePtr, length);
            var destinationSpan = new Span<byte>(destinationPtr, length);
            sourceSpan.CopyTo(destinationSpan);
        }
    }

    /// <summary>
    /// Serialize a promise into this blob.
    /// </summary>
    /// <param name="info">
    /// Preparation for serialization.  This blob must have been allocated
    /// to hold the number of bytes specified in 
    /// <see cref="PromiseSerializationInfo.TotalLength" />.
    /// </param>
    public unsafe void SaveSerialization(in PromiseSerializationInfo info)
    {
        int length = TotalLength;

        if (length < info.TotalLength) 
        {
            throw new InvalidOperationException(
                "Attempt to serialize a promise into blob storage " +
                "that is sized too small. ");
        }
            
        fixed (void* ptr = &this)
        {
            var buffer = new Span<byte>(ptr, length);
            info.Serialize(buffer);
        }
    }

    /// <summary>
    /// De-serialize this blob into a <see cref="Promise" /> object.
    /// </summary>
    /// <param name="fixtures">
    /// Dependencies needed to revive the promise object,
    /// including the data schema registry.
    /// </param>
    /// <remarks>
    /// The promise object obtained from <see cref="Promise.Deserialize" />
    /// applied to this blob.
    /// </remarks>
    public readonly unsafe Promise RestoreObject(IPromiseDataFixtures fixtures)
    {
        int length = TotalLength;

        fixed (void* ptr = &this)
        {
            var buffer = new ReadOnlySpan<byte>(ptr, length);
            return Promise.Deserialize(fixtures, buffer);
        }
    }

    /// <summary>
    /// Initialize a memory area that has just been allocated
    /// to hold a blob.
    /// </summary>
    /// <remarks>
    /// "Initialization" here means only setting the minimal 
    /// header fields to record the correct size of the blob.
    /// The payload is not stored/copied at this point.
    /// </remarks>
    /// <param name="buffer">
    /// The extent of the memory area.
    /// </param>
    public static unsafe void Initialize(Span<byte> buffer)
    {
        fixed (void* p = buffer)
        {
            ref var blob = ref Unsafe.AsRef<PromiseSerializationHeader>(p);
            blob = PromiseSerializationHeader.AllocateForBlob(buffer.Length);
        }
    }

    public static PromiseBlob CreatePlaceholder(long length)
    {
        return new PromiseBlob
        {
            _header = PromiseSerializationHeader.AllocateForBlob(checked((int)length))
        };
    }
}
