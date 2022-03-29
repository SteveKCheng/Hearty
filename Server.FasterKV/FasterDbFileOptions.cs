using FASTER.core;
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
    /// Base-2 logarithm of the maximum amount of bytes of
    /// in-process memory to store the values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FASTER KV requires the memory capacity in bytes to be
    /// powers of 2.  Furthermore, it must be at least
    /// the size of the database's storage page, which is
    /// set in <see cref="PageLog2Size" />.
    /// See the documentation from
    /// FASTER KV for more details.
    /// </para>
    /// <para>
    /// If this property's value is less than the
    /// base-2 logarithm of the page size, it will be floored to that.
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
    /// Base-2 logarithm of the number of bytes 
    /// in a page in the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page size in bytes must be a power of 2.  It must be 
    /// at least the sector size of filesystem storage,
    /// typically 512 bytes.  See the documentation from
    /// FASTER KV for more details.
    /// </para>
    /// <para>
    /// A null value for this property means 
    /// the default page size of 2^25 bytes (32 MB), as recommended
    /// by the FASTER KV authors.  Any other value will be capped 
    /// to 2^26 bytes and floored to 2^9 bytes.
    /// </para>
    /// </remarks>
    public int? PageLog2Size { get; init; }

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

    /// <summary>
    /// Translate (the simplified) settings in this structure to 
    /// what FASTER KV's API accepts.
    /// </summary>
    internal LogSettings CreateFasterDbLogSettings()
    {
        var logSettings = new LogSettings();

        logSettings.PageSizeBits =
            Math.Min(26, Math.Max(9, this.PageLog2Size ?? 25));

        logSettings.MemorySizeBits =
            Math.Min(47, Math.Max(logSettings.PageSizeBits,
                                  this.MemoryLog2Capacity));

        if (this.Path is null)
        {
            logSettings.LogDevice = new NullDevice();
        }
        else
        {
            bool deleteOnClose = this.DeleteOnDispose;
            var path = this.Path;
            if (string.IsNullOrEmpty(path))
            {
                path = System.IO.Path.GetTempFileName();
                deleteOnClose = true;
            }

            logSettings.LogDevice = Devices.CreateLogDevice(
                    logPath: path,
                    preallocateFile: this.Preallocate,
                    deleteOnClose: deleteOnClose);

        }

        return logSettings;
    }
}
