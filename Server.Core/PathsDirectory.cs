using Hearty.BTree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Abstraction to access promises by named paths.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PromisePath" /> represents a path that may be
/// assigned to a promise.  This class holds a collection of them.
/// </para>
/// <para>
/// A promise may be assigned more than one path, so effectively
/// paths form secondary indices (like in SQL databases) to promises
/// whose actual data are stored in <see cref="PromiseStorage" />.
/// </para>
/// </remarks>
public abstract class PathsDirectory
{
    /// <summary>
    /// Register a new path to a promise.
    /// </summary>
    public abstract void AddPath(ref PromisePath path, PromiseId id);

    /// <summary>
    /// Remove a previously registered path to a promise.
    /// </summary>
    public abstract void RemovePath(PromisePath path, PromiseId id);

    /// <summary>
    /// Query the promise ID behind a path, if it exists.
    /// </summary>
    public abstract bool TryGetPath(PromisePath path, out PromiseId id);

    /// <summary>
    /// List all the sub-paths registered under a folder path.
    /// </summary>
    public abstract IReadOnlyList<KeyValuePair<PromisePath, PromiseId>>
        ListSubpaths(PromisePath startPath, int suggestedCount);
}
