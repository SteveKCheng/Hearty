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
}
