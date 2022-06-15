using System;

namespace Hearty.Scheduling;

/// <summary>
/// Describes a flow that is being activated or de-activating
/// from its parent scheduling group.
/// </summary>
public readonly struct SchedulingActivationEventArgs
{
    /// <summary>
    /// Whether the child flow if being activated or de-activated.
    /// </summary>
    public bool Activated { get; }

    /// <summary>
    /// Whether the child flow considers its de-activation
    /// to be temporary.
    /// </summary>
    /// <remarks>
    /// A de-activation may be considered to be temporary
    /// if the flow is about to de-queue an item, but 
    /// whose value is not yet available because it is
    /// waiting for asynchronous processing.  A non-temporary
    /// de-activation would be for a queue that is exhausted
    /// of items.  This distinction is useful for expiring
    /// empty queues automatically.
    /// </remarks>
    public bool IsTemporary { get; }

    /// <summary>
    /// Counts how many activation or de-activation
    /// events have occurred so far including this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This number is a version counter that orders the activation
    /// and de-activation events.  In a concurrent setting, the
    /// event handler is not guaranteed to be invoked in sequence
    /// in the same order that the events occur.
    /// </para>
    /// <para>
    /// This number will silently wrap around when the
    /// count exceeds <see cref="uint.MaxValue" />.
    /// </para>
    /// </remarks>
    public uint Counter { get; }

    /// <summary>
    /// The attached object that was passed to 
    /// <see cref="SchedulingGroup{T}.AdmitChild" />
    /// for the flow being activated or de-activated.
    /// </summary>
    public object? Attachment { get; }

    internal SchedulingActivationEventArgs(bool activated, 
                                           bool isTemporary,
                                           uint counter, 
                                           object? attachment)
    {
        Activated = activated;
        IsTemporary = isTemporary;
        Counter = counter;
        Attachment = attachment;
    }

    /// <summary>
    /// Whether this event is (likely to be) newer than
    /// another event with the given counter, tolerating
    /// overflows of the counter.
    /// </summary>
    /// <param name="counter">
    /// Counter (version) of another event to compare against.
    /// </param>
    public bool IsNewerThan(uint counter)
    {
        return unchecked((int)(Counter - counter)) > 0;
    }
}
