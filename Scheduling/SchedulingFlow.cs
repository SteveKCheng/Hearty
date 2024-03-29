﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Hearty.Scheduling;

/// <summary>
/// Represents a generic child queue in a parent scheduling group,
/// for fair job scheduling.
/// </summary>
/// <remarks>
/// <para>
/// Most of the data members in this class can be considered to
/// be controlled by the parent scheduling group.  They need to
/// be manipulated while the parent scheduling group is locked
/// to ensure consistency between all children of the scheduling
/// group.  
/// </para>
/// <para>
/// Thus only a few protected methods exist, solely for the child
/// queue to notify the parent scheduling group in response to
/// an external (asynchronous) event.
/// </para>
/// <para>
/// Admitting a queue to be managed in a parent scheduling group,
/// or changing its weight there, requires the "permission" of
/// the parent, and so there are no protected or public methods
/// to do that here.
/// </para>
/// </remarks>
public abstract class SchedulingFlow<T>
{
    /// <summary>
    /// Backing field for <see cref="Parent" />.
    /// </summary>
    /// <remarks>
    /// That the parent scheduling group can change after 
    /// construction of this object introduces concurrency hazards.
    /// To reliably prevent them, a thread may only change this 
    /// field while it holds the lock for the current 
    /// parent scheduling group; and that change must be setting
    /// it to null.  Then, a null parent can be transitioned
    /// to a non-null parent by atomic compare-and-exchange.
    /// There cannot be a direct transition from one parent
    /// to another.
    /// </remarks>
    private SchedulingGroup<T>? _parent;

    /// <summary>
    /// The queue group that owns this child queue.
    /// </summary>
    protected internal SchedulingGroup<T>? Parent => _parent;

    /// <summary>
    /// The index of this child queue in the parent's priority heap.
    /// </summary>
    /// <remarks>
    /// The parent will update this index if the child gets re-arranged
    /// with the other children inside the priority heap.
    /// </remarks>
    internal int PriorityHeapIndex { get; set; }

    /// <summary>
    /// Get or set the current balance of abstract time remaining
    /// that this child queue is entitled to in fair job scheduling.
    /// </summary>
    /// <remarks>
    /// This balance should be decreased by the charge for each job
    /// as it is de-queued.  The parent will set this property
    /// when the balance needs to be re-filled or adjusted.
    /// </remarks>
    public int Balance { get; internal set; }

    /// <summary>
    /// Whether this queue is active, i.e. it may have a job available
    /// from the next call to <see cref="TryTakeItem(out T, out int)" />.
    /// </summary>
    public bool IsActive => PriorityHeapIndex >= 0;

    /// <summary>
    /// Backing field for <see cref="Weight" />.
    /// </summary>
    private int _weight;

    /// <summary>
    /// The reciprocal of the weight multiplied by 2^20 * 2^7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This quantity is cached only to avoid expensive integer
    /// division when adjusting balances (after each job charge).
    /// </para>
    /// <para>
    /// 2^7 is the maximum weight, and 2^20 is a scaling factor
    /// for implementing division in fixed-point arithmetic.
    /// </para>
    /// </remarks>
    private int _inverseWeight;

    /// <summary>
    /// The snapshot of <see cref="SchedulingGroup{T}._countRefills"/>
    /// when this child queue is de-activated.
    /// </summary>
    /// <remarks>
    /// This member determines when <see cref="Balance" />
    /// has to be re-filled if this child queue re-activates
    /// after balances on sibling queues have been refilled earlier.
    /// </remarks>
    internal uint RefillEpoch { get; set; }

    /// <summary>
    /// A weight that effectively multiplies the amount of credits 
    /// this child queue receives on each time slice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This weight is always between 1 and 128 inclusive.
    /// The default weight is 32.
    /// </para>
    /// <para>
    /// For important theoretical and practical reasons, this weight
    /// is applied through division not multiplication: see
    /// <see cref="_inverseWeight" />.
    /// </para>
    /// </remarks>
    public int Weight
    {
        get => _weight;
        internal set
        {
            _weight = value;
            _inverseWeight = (1 << (7 + InverseWeightLogScale)) / value;
        }
    }

    /// <summary>
    /// Log to the base 2 of the scaling factor applied to
    /// <see cref="_inverseWeight" />.
    /// </summary>
    private const int InverseWeightLogScale = 20;

    /// <summary>
    /// Compute a charge to this queue's balance 
    /// adjusted for "inflation" or "deflation",
    /// depending on the weight.
    /// </summary>
    /// <remarks>
    /// This arithmetic is done in "half 64-bit", which has
    /// has essentially no penalty in modern CPUs.
    /// </remarks>
    internal long ComputeDeflatedCharge(int charge)
        => ((long)charge * _inverseWeight) >> InverseWeightLogScale;

    /// <summary>
    /// Set to true when <see cref="Activate" /> is called.
    /// </summary>
    /// <remarks>
    /// This flag is needed by 
    /// <see cref="SchedulingGroup{T}.TryTakeItem(out T, out int)" />
    /// to avoid accidentally de-activating this child queue
    /// if there is a race with the user trying to re-activating it.
    /// </remarks>
    internal bool WasActivated { get; set; }

    /// <summary>
    /// Prepare a new child queue.
    /// </summary>
    protected SchedulingFlow()
    {
        PriorityHeapIndex = -1;

        // This value will be capped when this instance is admitted
        // to a scheduling group.
        Balance = int.MaxValue;

        _weight = 1 << 5;
        _inverseWeight = 1 << (7 + InverseWeightLogScale - 5);
    }

    /// <summary>
    /// Pull out the next item from this scheduling flow.
    /// </summary>
    /// <param name="item">
    /// Set to the retrieved item when this method
    /// returns true; otherwise set to the default value.
    /// </param>
    /// <param name="charge">The amount of credit to charge
    /// to the abstract queue where the item is coming from,
    /// to effect fair scheduling.
    /// </param>
    /// <remarks>
    /// If this method returns null, the abstract queue will be 
    /// temporarily de-activated by the caller, so the system
    /// avoid polling repeatedly for work.  No further
    /// calls to this method is made until re-activation.
    /// </remarks>
    /// <returns>
    /// The item that the fair scheduling system should be
    /// processing next, or null if this flow 
    /// currently has no item to process.  
    /// </returns>
    protected abstract bool TryTakeItem(
        [MaybeNullWhen(false)] out T item, out int charge);

    /// <summary>
    /// Invokes the protected method <see cref="TryTakeItem(out T, out int)" />
    /// for <see cref="SchedulingGroup{T}" />.
    /// </summary>
    internal bool TryTakeItemToParent(
        [MaybeNullWhen(false)] out T item, out int charge) 
        => TryTakeItem(out item, out charge);

    /// <summary>
    /// Ensure this scheduling unit is considered active for scheduling
    /// within its parent group.
    /// </summary>
    /// <remarks>
    /// If this instance currently has no parent scheduling group, 
    /// this method does nothing.
    /// </remarks>
    protected void Activate() => Parent?.ActivateChild(this);

    /// <summary>
    /// Exclude this scheduling unit from being actively considered 
    /// for scheduling within its parent group.
    /// </summary>
    /// <remarks>
    /// If this instance currently has no parent scheduling group, 
    /// this method does nothing.
    /// </remarks>
    /// <param name="temporary">
    /// Whether this de-activation should be considered temporary.
    /// See <see cref="SchedulingActivationEventArgs.IsTemporary" />.
    /// </param>
    protected void Deactivate(bool temporary = false) 
        => Parent?.DeactivateChild(this, temporary);

    /// <summary>
    /// Adjust the debit balance of this scheduling unit to affect
    /// its priority amongst other units in its parent scheduling group.
    /// </summary>
    /// <param name="debit">
    /// The amount to add to <see cref="Balance" />.
    /// </param>
    /// <remarks>
    /// If this method has no effect if there is currently no
    /// parent scheduling group.  However, it does have an effect
    /// even if this scheduling flow is inactive: the adjusted
    /// balance will affect how it is prioritized against sibling
    /// scheduling flows when it becomes active again.
    /// </remarks>
    protected void AdjustBalance(int debit) 
        => Parent?.AdjustChildBalance(this, debit);

    /// <summary>
    /// Stop becoming activated as a child queue of a parent scheduling group.
    /// </summary>
    protected internal void LeaveParent()
    {
        var oldParent = _parent;
        if (oldParent is not null)
            oldParent.DeactivateChildAndDisown(this, ref _parent);
    }

    /// <summary>
    /// Set the parent scheduling group.
    /// </summary>
    /// <remarks>
    /// This instance must not have any other parent currently.
    /// It will not be activated immediately in its new parent 
    /// scheduling group.
    /// </remarks>
    /// <param name="parent">
    /// The scheduling group to assume as this instance's new parent.
    /// </param>
    internal void SetParent(SchedulingGroup<T> parent)
    {
        if (Interlocked.CompareExchange(ref _parent, parent, null) is not null)
        {
            throw new InvalidOperationException(
                "This attempt to change the parent scheduling group failed " +
                "because another thread concurrently did the same. ");
        }
    }

    /// <summary>
    /// Reference to an arbitrary object that the parent's 
    /// callback can consult when this child flow
    /// activates or de-activates.
    /// </summary>
    internal object? Attachment { get; set; }
}
