using System;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Credit-based scheduling from a set of <see cref="SchedulingUnit" />.
    /// </summary>
    public class SchedulingGroup<TJob>
    {
        /// <summary>
        /// Organizes the child queues so that the 
        /// active child with the highest credit balance
        /// can be quickly accessed.
        /// </summary>
        private IntPriorityHeap<SchedulingUnit<TJob>> _priorityHeap;

        /// <summary>
        /// List of all the child queues managed by this instance.
        /// </summary>
        /// <remarks>
        /// In this array, the child queues can be in any order.
        /// Thus, when deleting a member, the last member 
        /// can be swapped in to the vacated slot, so that
        /// all the members occupy consecutive slots starting
        /// from the beginning of this array.  The slots
        /// on and after the index <see cref="_numChildren" />
        /// are set to null; the first such slot can be taken
        /// when a new member has to be set.
        /// </remarks>
        private SchedulingUnit<TJob>?[] _allChildren;

        /// <summary>
        /// The number of child queues managed by this instance
        /// currently.
        /// </summary>
        private int _numChildren = 0;

        protected const int MaxCapacity = 1024;

        protected SchedulingGroup(int capacity)
        {
            if (capacity < 0 || capacity > MaxCapacity)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _priorityHeap = new IntPriorityHeap<SchedulingUnit<TJob>>(
                (ref SchedulingUnit<TJob> item, int index) => item.PriorityHeapIndex = index);

            _allChildren = capacity > 0 ? new SchedulingUnit<TJob>?[capacity] 
                                        : Array.Empty<SchedulingUnit<TJob>?>();
        }

        /// <summary>
        /// Re-fill balances on all child queues when none are eligible
        /// to be scheduled.
        /// </summary>
        /// <returns>
        /// Whether there is at least one active child queue
        /// (after re-filling).
        /// </returns>
        private bool RefillBalances()
        {
            bool hasActiveChild = false;

            // Calculate the highest non-positive balance
            // among all active child queues
            int best = int.MinValue;
            for (int i = 0; i < _allChildren.Length; ++i)
            {
                var child = _allChildren[i];
                if (child != null && child.IsActive && child.Balance <= 0)
                {
                    hasActiveChild = true;
                    best = Math.Max(best, child.Balance);
                }
            }

            if (!hasActiveChild)
                return false;

            _priorityHeap.Clear();

            for (int i = 0; i < _allChildren.Length; ++i)
            {
                var child = _allChildren[i];
                if (child != null)
                {
                    int oldBalance = child.Balance;

                    // Brings the "best" child queue with previously non-positive
                    // balance to zero.  If an inactive child queue already had
                    // positive balance, reset it back to zero.
                    //
                    // Same as:
                    //   balance = Math.Min(0, MiscArithmetic.SaturatingSubtract(balance, best))
                    int newBalance = oldBalance <= best ? oldBalance - best : 0;

                    // Then add a certain amount of credits to all queues.
                    newBalance += 10000;

                    child.Balance = newBalance;

                    if (child.IsActive && newBalance > 0)
                    {
                        _priorityHeap.Insert(newBalance, child);
                        UpdateForAverageBalance(child, oldBalance, newBalance);
                    }
                }
            }

            // There must be at least one queue above that now has positive balance.
            return true;
        }

        /// <summary>
        /// Take a job from the child queue that currently has one,
        /// and has the highest balance.
        /// </summary>
        /// <returns>
        /// The de-queued job, or null if no child queue can currently 
        /// supply one.
        /// </returns>
        protected TJob? TakeJob()
        {
            TJob? job = default;

            do
            {
                // If no child queues are eligible for de-queuing,
                // that means they are all inactive or they have
                // run out of credits.  Try to re-fill their credits.
                if (_priorityHeap.IsEmpty && !RefillBalances())
                    break;

                var (oldBalance, child) = _priorityHeap[0];
                job = child.TakeJob(out int charge);

                if (job is null)
                    child.IsActive = false;

                int newBalance = MiscArithmetic.SaturatingSubtract(oldBalance, charge);
                child.Balance = newBalance;

                if (job is not null && newBalance > 0)
                {
                    _priorityHeap.ChangeKey(0, newBalance);
                    UpdateForAverageBalance(child, oldBalance, newBalance);
                }
                else
                {
                    _priorityHeap.TakeMaximum();
                    UpdateForAverageBalance(child, oldBalance, 0);
                }
                    
            } while (job is null);

            return job;
        }

        /// <summary>
        /// Add a new (abstract) child queue to this job queue group.
        /// </summary>
        /// <param name="jobSource">
        /// The source of the jobs in the new (abstract) child queue.
        /// </param>
        protected SchedulingUnit<TJob> AddChild(IJobSource<TJob> jobSource)
        {
            var allChildren = _allChildren;
            int index = _numChildren;
            if (index == allChildren.Length)
            {
                if (index >= MaxCapacity)
                    throw new InvalidOperationException("Cannot add a child queue because there is no more capacity. ");

                // Grow capacity of array by 1/2
                int newCapacity = Math.Min(index + (index >> 1), MaxCapacity);
                var newArray = new SchedulingUnit<TJob>?[newCapacity];
                Array.Copy(allChildren, newArray, allChildren.Length);
                _allChildren = allChildren = newArray;
            }

            var child = new SchedulingUnit<TJob>(this, jobSource) { Index = index };
            _numChildren = index + 1;
            allChildren[index] = child;

            int newBalance = GetAverageBalance(child);
            UpdateForAverageBalance(child, 0, newBalance);
            child.Balance = newBalance;

            return child;
        }

        /// <summary>
        /// Remove the child queue that had been added by <see cref="AddChild" />
        /// earlier.
        /// </summary>
        /// <param name="child">
        /// The child queue to remove, which must have been created by this
        /// parent.
        /// </param>
        private void RemoveChild(SchedulingUnit<TJob> child)
        {
            var index = child.Index;
            if (index < 0 || child.Parent != this)
                throw new ArgumentException("Child queue is not present in the parent queue. ", nameof(child));

            var allChildren = _allChildren;
            child.Index = -1;

            if (child.PriorityHeapIndex >= 0)
            {
                _priorityHeap.Delete(child.PriorityHeapIndex);
                UpdateForAverageBalance(child, child.Balance, 0);
            }

            var lastIndex = --_numChildren;
            if (lastIndex > 0)
            {
                ref SchedulingUnit<TJob>? lastChild = ref allChildren[lastIndex];
                allChildren[index] = lastChild;
                lastChild!.Index = index;
                lastChild = null;
            }
        }

        /// <summary>
        /// Ensure a child is marked active and put it into
        /// the priority heap if it has positive balance.
        /// </summary>
        /// <param name="child">
        /// The child scheduling unit.  This instance
        /// must be its parent.
        /// </param>
        internal void ActivateChild(SchedulingUnit<TJob> child)
        {
            if (!child.IsActive)
            {
                child.IsActive = true;
                if (child.Balance > 0)
                {
                    _priorityHeap.Insert(child.Balance, child);
                    UpdateForAverageBalance(child, 0, child.Balance);
                }
            }
        }

        /// <summary>
        /// De-activate a child and take it out of the priority
        /// heap if it is there.
        /// </summary>
        /// <param name="child">
        /// The child scheduling unit.  This instance
        /// must be its parent.
        /// </param>
        internal void DeactivateChild(SchedulingUnit<TJob> child)
        {
            if (child.IsActive)
            {
                child.IsActive = false;
                _priorityHeap.Delete(child.PriorityHeapIndex);
                UpdateForAverageBalance(child, child.Balance, 0);
            }
        }

        /// <summary>
        /// Change the debit balance of a child and re-schedule
        /// it into the priority heap if necessary.
        /// </summary>
        /// <param name="child">
        /// The child scheduling unit.  This instance
        /// must be its parent.
        /// </param>
        /// <param name="debit">
        /// The amount to add to the child's debit balance.
        /// </param>
        internal void AdjustChildBalance(SchedulingUnit<TJob> child, int debit)
        {
            int oldBalance = child.Balance;
            int newBalance = MiscArithmetic.SaturatingAdd(oldBalance, debit);

            if (child.IsActive)
            {
                if (oldBalance > 0)
                {
                    if (newBalance <= 0)
                        _priorityHeap.Delete(child.PriorityHeapIndex);
                    else
                        _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);
                }
                else if (newBalance > 0)
                {
                    _priorityHeap.Insert(newBalance, child);
                }

                UpdateForAverageBalance(child, oldBalance, newBalance);
            }

            child.Balance = newBalance;
        }

        /// <summary>
        /// The sum of unweighted balances that are positive
        /// from active child queues.
        /// </summary>
        /// <remarks>
        /// This quantity is updated after every change in balance,
        /// so that the average can be computed in O(1) time
        /// when new child queues are added.
        /// </remarks>
        private long _sumUnweightedBalances;

        /// <summary>
        /// The number of positive balances summed inside <see cref="_sumUnweightedBalances" />.
        /// </summary>
        /// <remarks>
        /// This is the denominator used to calculate the average balance.
        /// </remarks>
        private int _countPositiveBalances;

        /// <summary>
        /// Update the running sum/average of positive, active balances.
        /// </summary>
        /// <param name="oldBalance">The old balance to remove from the
        /// running average.
        /// </param>
        /// <param name="newBalance">The new balance to add to the running
        /// average.
        /// </param>
        /// <param name="child">The child queue that the balances to update
        /// apply to.
        /// </param>
        private void UpdateForAverageBalance(SchedulingUnit<TJob> child, 
                                             int oldBalance, 
                                             int newBalance)
        {
            long sum = _sumUnweightedBalances;
            sum -= oldBalance > 0 ? oldBalance : 0;
            sum += newBalance > 0 ? newBalance : 0;
            _sumUnweightedBalances = sum;

            _countPositiveBalances += (newBalance > 0 ? 1 : 0) 
                                    - (oldBalance > 0 ? 1 : 0);
        }

        /// <summary>
        /// Return the running average of positive balances among
        /// all active child queues.
        /// </summary>
        /// <param name="child">The child queue that the new average balance
        /// will be applied to.  
        /// </param>
        /// <returns>
        /// The suggested balance to set on <paramref name="child"/>, based
        /// on the running average.
        /// </returns>
        private int GetAverageBalance(SchedulingUnit<TJob> child)
        {
            int unweightedBalance = (int)(_sumUnweightedBalances / _countPositiveBalances);
            return unweightedBalance;
        }
    }
}
