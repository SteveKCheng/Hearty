using System;
using FASTER.core;

namespace Hearty.Server.FasterKV;

/// <summary>
/// Comparer to use <see cref="PromiseId" /> as a key in a FASTER KV
/// database efficiently.
/// </summary>
public sealed class FasterDbPromiseComparer : IFasterEqualityComparer<PromiseId>
{
    /// <inheritdoc cref="IFasterEqualityComparer{T}.Equals" />
    public bool Equals(ref PromiseId k1, ref PromiseId k2)
        => k1 == k2;

    /// <inheritdoc cref="IFasterEqualityComparer{T}.GetHashCode64" />
    public long GetHashCode64(ref PromiseId k) => (long)k.RawInteger;
}
