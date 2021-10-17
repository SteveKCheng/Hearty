using System;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents a generic child queue in a parent queue system
    /// for fair job scheduling.
    /// </summary>
    public sealed class SchedulingUnit<TJob>
    {
        /// <summary>
        /// The queue group that owns this child queue.
        /// </summary>
        public SchedulingGroup<TJob> Parent { get; }

        /// <summary>
        /// The index of this child in <see cref="SchedulingGroup._allChildren" />,
        /// retained so that it can be deleted.
        /// </summary>
        /// <remarks>
        /// This index is -1 if the child is no longer present in the
        /// parent <see cref="SchedulingGroup" />.
        /// </remarks>
        internal int Index { get; set; }

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
        /// from the next call to <see cref="TakeJob" />.
        /// </summary>
        public bool IsActive { get; internal set; }

        /// <summary>
        /// Prepare a new child queue.
        /// </summary>
        /// <param name="jobSource">
        /// Provides the job items when the abstract child queue is "de-queued".
        /// </param>
        internal SchedulingUnit(SchedulingGroup<TJob> parent, IJobSource<TJob> jobSource)
        {
            Parent = parent;
            JobSource = jobSource;
        }

        /// <summary>
        /// Pulls out a job to process when requested
        /// by the parent <see cref="SchedulingGroup" />.
        /// </summary>
        public IJobSource<TJob> JobSource { get; }

        /// <summary>
        /// Calls <see cref="IJobSource.TakeJob" />.
        /// </summary>
        /// <param name="charge">The amount of credit to charge
        /// to this child queue for the new job.
        /// </param>
        /// <returns>
        /// The job that the parent <see cref="SchedulingGroup" />
        /// should be processing next, or null if there is none
        /// from this child queue.
        /// </returns>
        internal TJob? TakeJob(out int charge)
            => JobSource.TakeJob(out charge);

        /// <summary>
        /// Ensure this scheduling unit is considered active for scheduling
        /// within its parent group.
        /// </summary>
        public void Activate() => Parent.ActivateChild(this);

        /// <summary>
        /// Exclude this scheduling unit from being actively considered 
        /// for scheduling within its parent group.
        /// </summary>
        public void Deactivate() => Parent.DeactivateChild(this);

        /// <summary>
        /// Adjust the debit balance of this scheduling unit to affect
        /// its priority amongst other units in its parent scheduling group.
        /// </summary>
        /// <param name="debit">
        /// The amount to add to <see cref="Balance" />.
        /// </param>
        public void AdjustBalance(int debit) => Parent.AdjustChildBalance(this, debit);
    }
}
