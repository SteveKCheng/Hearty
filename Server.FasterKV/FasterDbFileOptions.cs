using System;

namespace Hearty.Server.FasterKV;

/// <summary>
/// Settings for file storage for a database backed by FASTER KV.
/// </summary>
public readonly struct FasterDbFileOptions
{
    /// <summary>
    /// The path to the file for backing storage.
    /// </summary>
    /// <remarks>
    /// If null, no file is used for backing storage.
    /// If empty, a temporary file will be created.
    /// </remarks>
    public string? Path { get; init; }

    /// <summary>
    /// Whether a new file should have its storage blocks pre-allocated.
    /// </summary>
    public bool Preallocate { get; init; }

    /// <summary>
    /// Whether to delete the file when the FASTER KV database
    /// is disposed.
    /// </summary>
    public bool DeleteOnDispose { get; init; }

    /// <summary>
    /// Base-2 logarithm of the maximum amount of in-process
    /// memory to store the values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FASTER KV requires the memory capacity to be
    /// powers of 2.  Furthermore, it must be at least
    /// the size of the database's storage page, which is
    /// currently 2^25 bytes = 32MB.  
    /// See the documentation from
    /// FASTER KV for more details.
    /// </para>
    /// <para>
    /// If this property's value is less than the
    /// base-2 logarithm of the page size, i.e. less
    /// than 25, it will be floored to that.
    /// </para>
    /// <para>
    /// The memory capacity also cannot be greater than
    /// 2^47 bytes = 128 TB, as current CPUs also cannot
    /// address more than 256 TB of RAM.
    /// </para>
    /// <para>
    /// The memory capacity subject to this property
    /// does not count the memory used by the hash index,
    /// which is controlled by <see cref="HashIndexSize" />.
    /// </para>
    /// </remarks>
    public int MemoryLog2Capacity { get; init; }

    /// <summary>
    /// The number of buckets in the top-level hash index
    /// to allocate in the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each bucket occupies 64 bytes in in-process memory, 
    /// holding at most 7 entries.  Hash collisions
    /// may result in overflow buckets that also occupy
    /// 64 bytes each.  See the documentation from 
    /// FASTER KV for more details.
    /// </para>
    /// <para>
    /// This quantity will be floored to 256, i.e.
    /// the top-level buckets will take up at least 16KB.
    /// It will be capped at 2^40.
    /// </para>
    /// <para>
    /// In the most optimal case, 
    /// when there are no hash collisions that cause 
    /// overflow buckets to be allocated, such an index can hold
    /// 256 × 7 = 1792 items.
    /// </para>
    /// </remarks>
    public long HashIndexSize { get; init; }
}
