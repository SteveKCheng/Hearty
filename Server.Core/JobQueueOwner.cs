using System;
using System.Security.Claims;

namespace Hearty.Server;

/// <summary>
/// A simple realization of <see cref="IJobQueueOwner" />
/// wrapping a string, with no other metadata.
/// </summary>
public class JobQueueOwner : IJobQueueOwner
{
    /// <inheritdoc cref="IJobQueueOwner.Title" />
    public string Title { get; }

    /// <inheritdoc cref="IJobQueueOwner.Principal" />
    public ClaimsPrincipal? Principal { get; }

    public JobQueueOwner(string title, ClaimsPrincipal? principal = null)
    {
        Title = title;
        Principal = principal;
    }

    /// <inheritdoc cref="IEquatable{T}.Equals(T?)" />
    public bool Equals(IJobQueueOwner? other)
        => other is JobQueueOwner s && string.Equals(Title, s.Title);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is JobQueueOwner other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Title.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Title;

    /// <inheritdoc cref="IComparable{T}.CompareTo(T?)" />
    public int CompareTo(IJobQueueOwner? other)
    {
        // Order by .NET type name if types are different
        if (other is not JobQueueOwner s)
            return typeof(JobQueueOwner).FullName!.CompareTo(other?.GetType().FullName);

        return Title.CompareTo(s.Title);
    }
}
