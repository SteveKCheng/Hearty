using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace JobBank.Scheduling
{
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
    public abstract class SchedulingUnit<T>
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
        protected internal int Balance { get; internal set; }

        /// <summary>
        /// Whether this queue is active, i.e. it may have a job available
        /// from the next call to <see cref="TakeJob" />.
        /// </summary>
        protected internal bool IsActive => PriorityHeapIndex >= 0;

        /// <summary>
        /// Backing field for <see cref="Weight" />.
        /// </summary>
        private int _weight;

        /// <summary>
        /// Backing field for <see cref="ReciprocalWeight" />.
        /// </summary>
        private int _reciprocalWeight;

        /// <summary>
        /// A weight that is multiplies the amount of credits this child queue
        /// receives on each time slice.
        /// </summary>
        /// <remarks>
        /// This weight is always between 1 and 100 inclusive.
        /// </remarks>
        protected internal int Weight
        {
            get => _weight;
            internal set
            {
                _weight = value;
                _reciprocalWeight = (1 << ReciprocalWeightLogScale) / value;
            }
        }

        internal const int ReciprocalWeightLogScale = 20;

        /// <summary>
        /// The reciprocal of the weight multiplied by 2^20.
        /// </summary>
        /// <remarks>
        /// This quantity is cached only to avoid expensive integer
        /// division when updating averages of unweighted balances.
        /// </remarks>
        internal int ReciprocalWeight => _reciprocalWeight;

        /// <summary>
        /// Set to true when <see cref="Activate" /> is called.
        /// </summary>
        /// <remarks>
        /// This flag is needed by 
        /// <see cref="SchedulingGroup{T}.TakeJob" />
        /// to avoid accidentally de-activating this child queue
        /// if there is a race with the user trying to re-activating it.
        /// </remarks>
        internal bool WasActivated { get; set; }

        /// <summary>
        /// Prepare a new child queue.
        /// </summary>
        protected SchedulingUnit()
        {
            PriorityHeapIndex = -1;
            Weight = 1;
        }

        /// <summary>
        /// Pull out one job to execute for the fair scheduling system.
        /// </summary>
        /// <param name="charge">The amount of credit to charge
        /// to the abstract queue where the job is coming from,
        /// to effect fair scheduling.
        /// </param>
        /// <remarks>
        /// If this method returns null, the abstract queue will be 
        /// temporarily de-activated by the caller, so the system
        /// avoid polling repeatedly for work.  No further
        /// calls to this method is made until re-activation.
        /// </remarks>
        /// <returns>
        /// The job that the fair job scheduling system should be
        /// processing next, or null if this source instance
        /// currently has no job to process.  
        /// </returns>
        protected abstract bool TryTakeItem(
            [MaybeNullWhen(false)] out T item, out int charge);

        /// <summary>
        /// Invokes <see cref="IJobSource.TakeJob" />
        /// for <see cref="SchedulingGroup{T}"/>.
        /// </summary>
        /// <param name="charge">The amount of credit to charge
        /// to this child queue for the new job.
        /// </param>
        /// <returns>
        /// The job that the parent <see cref="SchedulingGroup" />
        /// should be processing next, or null if there is none
        /// from this child queue.
        /// </returns>
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
        protected void Deactivate() => Parent?.DeactivateChild(this);

        /// <summary>
        /// Adjust the debit balance of this scheduling unit to affect
        /// its priority amongst other units in its parent scheduling group.
        /// </summary>
        /// <param name="debit">
        /// The amount to add to <see cref="Balance" />.
        /// </param>
        /// <remarks>
        /// If this method has no effect if there is currently no
        /// parent scheduling group.
        /// </remarks>
        protected void AdjustBalance(int debit) => Parent?.AdjustChildBalance(this, debit);

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
    }
}
