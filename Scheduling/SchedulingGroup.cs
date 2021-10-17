using System;
using System.Diagnostics;

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
        /// <remarks>
        /// Child queues that are inactive are not put into the priority queue.
        /// </remarks>
        private IntPriorityHeap<SchedulingUnit<TJob>> _priorityHeap;

        /// <summary>
        /// List of all the child queues managed by this instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In this array, the child queues can be in any order
        /// as long as blank slots all occur after non-blank entries. </para>
        /// </para>
        /// <para>
        /// Thus, when deleting a member, the last member 
        /// can be swapped in to the vacated slot, so that
        /// all the members occupy consecutive slots starting
        /// from the beginning of this array.  The slots
        /// on and after the index <see cref="_countChildren" />
        /// are set to null; the first such slot can be taken
        /// when a new member has to be set.
        /// </para>
        /// </remarks>
        private SchedulingUnit<TJob>?[] _allChildren;

        /// <summary>
        /// The number of child queues managed by this instance
        /// currently.
        /// </summary>
        private int _countChildren = 0;

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
        private bool RefillBalancesAsNeeded()
        {
            if (_priorityHeap.IsEmpty)
                return false;

            int best = _priorityHeap.PeekMaximum().Key;
            if (best > 0)
                return true;

            var self_ = this;
            _priorityHeap.Initialize(_priorityHeap.Count, ref self_,
                static (ref SchedulingGroup<TJob> self, 
                        in Span<int> keys,
                        in Span<SchedulingUnit<TJob>> values) =>
                {
                    int best_ = keys[0];

                    for (int i = 0; i < keys.Length; ++i)
                    {
                        var child = values[i];
                        int oldBalance = child.Balance;

                        // Brings the "best" child queue to have a zero balance,
                        // while everything else remains zero or negative.
                        int newBalance = oldBalance - best_;

                        // Then add credits to the queue depending on its weight.
                        newBalance += 10000 * child.Weight;

                        child.Balance = newBalance;
                        keys[i] = newBalance;

                        self.UpdateForAverageBalance(child, oldBalance, newBalance);
                    }

                    return keys.Length;
                });

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
                if (!RefillBalancesAsNeeded())
                    break;

                var (oldBalance, child) = _priorityHeap[0];
                Debug.Assert(oldBalance > 0 && child.IsActive);

                job = child.TakeJob(out int charge);

                if (job is null)
                    DeactivateChild(child);

                int newBalance = MiscArithmetic.SaturatingSubtract(oldBalance, charge);
                child.Balance = newBalance;

                if (job is not null)
                {
                    _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);
                    UpdateForAverageBalance(child, oldBalance, newBalance);
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
        /// <remarks>
        /// The new abstract child queue, initially considered inactive.
        /// </remarks>
        protected SchedulingUnit<TJob> CreateChild(IJobSource<TJob> jobSource, int weight)
        {
            var child = new SchedulingUnit<TJob>(this, jobSource) 
            { 
                Weight = weight,
                Balance = GetAverageBalance(weight)
            };

            return child;
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
                var allChildren = _allChildren;
                int index = _countChildren;
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

                allChildren[index] = child;
                child.Index = index;
                _countChildren = index + 1;

                _priorityHeap.Insert(child.Balance, child);
                UpdateForAverageBalance(child, 0, child.Balance);
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
                _priorityHeap.Delete(child.PriorityHeapIndex);
                UpdateForAverageBalance(child, child.Balance, 0);
                
                // Delete the child's entry by swapping it
                // with the last non-blank entry in the array.
                int index = child.Index;
                var lastIndex = --_countChildren;
                var allChildren = _allChildren;
                ref var lastChild = ref allChildren[lastIndex];
                allChildren[index] = lastChild;
                lastChild!.Index = index;
                lastChild = null;
                child.Index = -1;
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
                _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);
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
            static long GetUnweightedBalance(SchedulingUnit<TJob> child, int balance)
                => ((long)balance * child.ReciprocalWeight) 
                    >> SchedulingUnit<TJob>.ReciprocalWeightLogScale;

            long sum = _sumUnweightedBalances;
            sum -= oldBalance > 0 ? GetUnweightedBalance(child, oldBalance) : 0;
            sum += newBalance > 0 ? GetUnweightedBalance(child, newBalance) : 0;
            _sumUnweightedBalances = sum;

            _countPositiveBalances += (newBalance > 0 ? 1 : 0) 
                                    - (oldBalance > 0 ? 1 : 0);
        }

        /// <summary>
        /// Return the running average of positive balances among
        /// all active child queues.
        /// </summary>
        /// <param name="weight">
        /// The weight to apply to the returned balance,
        /// same as <see cref="SchedulingUnit{TJob}.Weight" />
        /// for the new child queue.
        /// </param>
        /// <returns>
        /// The suggested balance to set on a new child queue, based
        /// on the running average.
        /// </returns>
        private int GetAverageBalance(int weight)
        {
            if (_countPositiveBalances <= 0)
                return 10000 * weight;

            long unweightedBalance = _sumUnweightedBalances / _countPositiveBalances;
            long balance = unweightedBalance * weight;
            return balance <= int.MaxValue ? (int)balance : int.MaxValue;
        }
    }
}
