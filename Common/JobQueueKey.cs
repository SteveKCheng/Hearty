using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Hearty.Common;

/// <summary>
/// A unique identifier for a queue where jobs may be posted.
/// </summary>
/// <remarks>
/// <para>
/// This is an immutable type, used by both clients and servers.
/// It is essentially a tuple of the queue owner, priority class
/// and cohort, where each part may be unspecified and left
/// to be defaulted by the server.
/// </para>
/// <para>
/// A default-constructed instance leaves all three parts unspecified.
/// </para>
/// </remarks>
[DebuggerDisplay($"{{{nameof(Priority)}}}; {{{nameof(Owner)}}}: {{{nameof(Cohort)}}}")]
public readonly struct JobQueueKey : IComparable<JobQueueKey>
                                   , IEquatable<JobQueueKey>
{
    /// <summary>
    /// Names the owner of the queue.
    /// </summary>
    /// <remarks>
    /// Owners are specified by strings (typically a user identifier)
    /// because that conveniences most clients. 
    /// Implementation of the job queues, on servers, may attach
    /// additional information about the owner, which is not expressed
    /// here.
    /// </remarks>
    public string? Owner { get; }

    /// <summary>
    /// The desired priority class of the queue.
    /// </summary>
    /// <remarks>
    /// Priority classes are numbered from 0 to the number of
    /// priority classes available minus one.  If this member
    /// is null, i.e. the priority class is not specified,
    /// the job queue system should take its default priority
    /// when adding a job, and search over all priorities
    /// if querying.
    /// </remarks>
    public int? Priority => _priority > 0 ? _priority - 1 : null;

    /// <summary>
    /// The name of the queue that distinguishes it within
    /// the set of queues with the same owner and priority class.
    /// </summary>
    /// <remarks>
    /// There can be multiple queues filed under the same 
    /// owner and priority, e.g. to serve different clients
    /// connecting with the same owner identity.  For conciseness,
    /// such queues are termed "cohorts".
    /// </remarks>
    public string? Cohort { get; }

    /// <summary>
    /// Backing field for the <see cref="Priority"/> property.
    /// </summary>
    /// <remarks>
    /// For compactness, priorities (which may not be negative) 
    /// are stored biased by one and the null value is stored
    /// as zero.
    /// </remarks>
    private readonly int _priority;

    /// <summary>
    /// Construct with the three components of the queue key,
    /// each optional.
    /// </summary>
    /// <param name="owner">Names the owner of the queue. </param>
    /// <param name="priority">The desired priority class of the queue. </param>
    /// <param name="cohort">The name of the queue that distinguishes it within
    /// the set of queues with the same owner and priority class.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="priority"/> is negative.
    /// </exception>
    public JobQueueKey(string? owner, int? priority, string? cohort)
    {
        if (priority is int value && value < 0)
        {
            throw new ArgumentOutOfRangeException(
                        nameof(priority),
                        "Priority class may not be negative. ");
        }

        Owner = owner;
        _priority = priority.HasValue ? priority.GetValueOrDefault() + 1 : 0;
        Cohort = cohort;
    }

    /// <inheritdoc cref="IComparable{T}.CompareTo" />
    public int CompareTo(JobQueueKey other)
    {
        int c = string.CompareOrdinal(Owner, other.Owner);
        if (c != 0)
            return c;

        c = _priority.CompareTo(other._priority);
        if (c != 0)
            return c;

        c = string.CompareOrdinal(Cohort, other.Cohort);
        return c;
    }

    /// <inheritdoc cref="IEquatable{T}.Equals" />
    public bool Equals(JobQueueKey other)
        => CompareTo(other) == 0;

    /// <inheritdoc cref="object.Equals(object?)" />
    public override bool Equals([NotNullWhen(true)] object? obj)
        => obj is JobQueueKey other && Equals(other);

    /// <inheritdoc cref="object.GetHashCode" />
    public override int GetHashCode()
    {
        return HashCode.Combine(Owner?.GetHashCode() ?? 0,
                                (uint)_priority * 2654435761u,
                                Cohort?.GetHashCode() ?? 0);
    }

    /// <summary>
    /// Returns whether all the corresponding 
    /// components of two keys are equal.
    /// </summary>
    public static bool operator ==(JobQueueKey left, JobQueueKey right)
        => left.Equals(right);

    /// <summary>
    /// Returns whether at least one corresponding component
    /// in two keys are unequal.
    /// </summary>
    public static bool operator !=(JobQueueKey left, JobQueueKey right)
        => !left.Equals(right);
}
