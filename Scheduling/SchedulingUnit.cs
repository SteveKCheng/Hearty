using System;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Abstraction for the fair job scheduling system to pull out
    /// one job to execute.
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
    public delegate TJob? JobSourceDelegate<TJob>(object? state, out int charge);

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
        public bool IsActive => PriorityHeapIndex >= 0;

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
        public int Weight
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
        /// <see cref="SchedulingGroup{TJob}.TakeJob" />
        /// to avoid accidentally de-activating this child queue
        /// if there is a race with the user trying to re-activating it.
        /// </remarks>
        internal bool WasActivated { get; set; }

        /// <summary>
        /// Prepare a new child queue.
        /// </summary>
        /// <param name="jobSource">
        /// Provides the job items when the abstract child queue is "de-queued".
        /// </param>
        internal SchedulingUnit(SchedulingGroup<TJob> parent,
                                JobSourceDelegate<TJob> jobSource,
                                object? state,
                                int weight)
        {
            if (weight < 1 || weight > 100)
                throw new ArgumentOutOfRangeException("The weight on a child queue is not between 1 to 100. ", (Exception?)null);

            Parent = parent;
            PriorityHeapIndex = -1;
            Weight = weight;

            _jobSource = jobSource;
            _jobSourceState = state;
        }

        private readonly object? _jobSourceState;

        private readonly JobSourceDelegate<TJob> _jobSource;

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
            => _jobSource.Invoke(_jobSourceState, out charge);

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
