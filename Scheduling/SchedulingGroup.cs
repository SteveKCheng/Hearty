using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Credit-based scheduling from a set of <see cref="SchedulingUnit" />.
    /// </summary>
    public partial class SchedulingGroup<TJob>
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
        /// Dummy object used for locking the state of this instance
        /// and the child queues it manages.
        /// </summary>
        private object SyncObject { get; }

        protected readonly int MaxCapacity = 1024;

        protected SchedulingGroup(int capacity)
        {
            if (capacity < 0 || capacity > MaxCapacity)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _priorityHeap = new IntPriorityHeap<SchedulingUnit<TJob>>(
                (ref SchedulingUnit<TJob> item, int index) => item.PriorityHeapIndex = index,
                capacity: capacity);

            SyncObject = new object();
        }

        /// <summary>
        /// Get the number of child queues managed by this instance
        /// that are currently active.
        /// </summary>
        protected int CountActiveSources => _priorityHeap.Count;

        /// <summary>
        /// Re-fill balances on all child queues when none are eligible
        /// to be scheduled.
        /// </summary>
        /// <returns>
        /// Whether there is at least one active child queue
        /// (after re-filling).
        /// </returns>
        private bool EnsureBalancesRefilled(bool reset)
        {
            if (_priorityHeap.IsEmpty)
                return false;

            int best = _priorityHeap.PeekMaximum().Key;
            if (best > 0 && !reset)
                return true;

            _countPositiveBalances = 0;
            _sumUnweightedBalances = 0;

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
                        int newBalance = MiscArithmetic.SaturatingSubtract(child.Balance, 
                                                                           best_);

                        // Then add credits to the queue depending on its weight.
                        newBalance += 10000 * child.Weight;

                        child.Balance = newBalance;
                        keys[i] = newBalance;

                        self.UpdateForAverageBalance(child, 0, newBalance);
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
        protected TJob? TakeJob(out int charge)
        {
            var syncObject = SyncObject;
            lock (syncObject)
            {
                while (EnsureBalancesRefilled(reset: false))
                {
                    var child = _priorityHeap.PeekMaximum().Value;

                    TJob? job;
                    do
                    {
                        child.WasActivated = false;

                        // Do not invoke user's function inside the lock
                        Monitor.Exit(syncObject);
                        try
                        {
                            job = child.TakeJobToParent(out charge);
                        }
                        finally
                        {
                            Monitor.Enter(syncObject);
                        }
                    } while (job is null && child.WasActivated);

                    if (job is not null)
                    {
                        int oldBalance = child.Balance;
                        int newBalance = MiscArithmetic.SaturatingSubtract(oldBalance, charge);
                        child.Balance = newBalance;

                        if (child.IsActive)
                        {
                            _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);
                            UpdateForAverageBalance(child, oldBalance, newBalance);
                        }

                        return job;
                    }

                    DeactivateChildCore(child);
                }
            }

            charge = 0;
            return default;
        }

        protected void ResetWeights(IEnumerable<KeyValuePair<SchedulingUnit<TJob>, int>> items)
        {
            // Defensive copy to avoid the values changing concurrently after
            // they are validated.
            var itemsArray = items.ToArray();

            foreach (var (child, weight) in itemsArray)
            {
                if (weight < 1 || weight > 100)
                    throw new ArgumentOutOfRangeException("The weight on a child queue is not between 1 to 100. ", (Exception?)null);

                if (child.Parent != this)
                    throw new ArgumentOutOfRangeException("The child queue does not belong to this parent. ", (Exception?)null);
            }

            lock (SyncObject)
            {
                foreach (var (child, weight) in itemsArray)
                    child.Weight = weight;

                EnsureBalancesRefilled(reset: true);
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
            bool firstActivated = false;

            lock (SyncObject)
            {
                child.WasActivated = true;

                if (!child.IsActive)
                {
                    child.Balance = GetAverageBalance(child.Weight);
                    _priorityHeap.Insert(child.Balance, child);
                    UpdateForAverageBalance(child, 0, child.Balance);

                    firstActivated = (CountActiveSources == 1);
                }
            }

            if (firstActivated)
                OnFirstActivated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Called upon activating a child queue when there were
        /// previously no existing active child queues.
        /// </summary>
        /// <remarks>
        /// This callback method can be used to propagate
        /// this <see cref="SchedulingGroup{TJob}" /> as a
        /// <see cref=""/>
        /// </remarks>
        protected event EventHandler<EventArgs>? OnFirstActivated;

        private void DeactivateChildCore(SchedulingUnit<TJob> child)
        {
            if (child.IsActive)
            {
                _priorityHeap.Delete(child.PriorityHeapIndex);
                UpdateForAverageBalance(child, child.Balance, 0);
            }
        }

        /// <summary>
        /// De-activate a child and take it out of the priority
        /// heap if it is there.
        /// </summary>
        /// <param name="child">
        /// The child scheduling unit.  This instance must be its parent.
        /// </param>
        internal void DeactivateChild(SchedulingUnit<TJob> child)
        {
            lock (SyncObject)
            {
                DeactivateChildCore(child);
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
            lock (SyncObject)
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
