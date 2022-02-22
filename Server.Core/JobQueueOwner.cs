using System;

namespace JobBank.Server;

/// <summary>
/// A simple realization of <see cref="IJobQueueOwner" />
/// wrapping a string, with no other metadata.
/// </summary>
public class JobQueueOwner : IJobQueueOwner
{
    /// <inheritdoc cref="IJobQueueOwner.Title" />
    public string Title { get; }

    public JobQueueOwner(string title) => Title = title;

    /// <inheritdoc cref="IEquatable{T}.Equals(T?)" />
    public bool Equals(IJobQueueOwner? other)
        => other is JobQueueOwner s && Title.Equals(s.Title);

    /// <inheritdoc />
    public override string ToString() => Title;

    /// <inheritdoc cref="IComparable{T}.CompareTo(T?)" />
    public int CompareTo(IJobQueueOwner? other)
    {
        if (other is JobQueueOwner s)
            return Title.CompareTo(s.Title);
        else
            return typeof(JobQueueOwner).FullName!.CompareTo(other?.GetType().FullName);
    }
}
