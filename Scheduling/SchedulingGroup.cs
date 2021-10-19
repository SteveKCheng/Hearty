using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Credit-based scheduling from a set of <see cref="SchedulingUnit" />.
    /// </summary>
    /// <remarks>
    /// The functionality of this class is available as protected,
    /// not public methods, so that a derived class can restrict
    /// some functionality depending on the application.  For instance,
    /// some scheduling groups might not allow non-equal weights
    /// on the child queues.
    /// </remarks>
    public partial class SchedulingGroup<T>
    {
        /// <summary>
        /// Organizes the child queues so that the 
        /// active child with the highest credit balance
        /// can be quickly accessed.
        /// </summary>
        /// <remarks>
        /// Child queues that are inactive are not put into the priority queue.
        /// </remarks>
        private IntPriorityHeap<SchedulingUnit<T>> _priorityHeap;

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

            _priorityHeap = new IntPriorityHeap<SchedulingUnit<T>>(
                (ref SchedulingUnit<T> item, int index) => item.PriorityHeapIndex = index,
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
                static (ref SchedulingGroup<T> self, 
                        in Span<int> keys,
                        in Span<SchedulingUnit<T>> values) =>
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
        protected bool TryTakeItem([MaybeNullWhen(false)] out T item, 
                                   out int charge)
        {
            var syncObject = SyncObject;
            lock (syncObject)
            {
                while (EnsureBalancesRefilled(reset: false))
                {
                    var child = _priorityHeap.PeekMaximum().Value;

                    bool hasItem;
                    do
                    {
                        child.WasActivated = false;

                        // Do not invoke user's function inside the lock
                        Monitor.Exit(syncObject);
                        try
                        {
                            hasItem = child.TryTakeItemToParent(out item, out charge);
                        }
                        finally
                        {
                            Monitor.Enter(syncObject);
                        }
                    } while (!hasItem && child.WasActivated);

                    if (hasItem)
                    {
                        int oldBalance = child.Balance;
                        int newBalance = MiscArithmetic.SaturatingSubtract(oldBalance, charge);
                        child.Balance = newBalance;

                        if (child.IsActive)
                        {
                            _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);
                            UpdateForAverageBalance(child, oldBalance, newBalance);
                        }

                        // CS8762: Parameter must have a non-null value when exiting in some condition.
                        // The C# compiler is not smart enough to deduce that hasItem == true
                        // means that item must have been set by TryTakeItemToParent above.
#pragma warning disable CS8762 
                        return true;
#pragma warning restore CS8762
                    }

                    DeactivateChildCore(child);
                }
            }

            charge = 0;
            item = default;
            return false;
        }

        /// <summary>
        /// Reset the weight on one child queue.
        /// </summary>
        protected void ResetWeight(SchedulingUnit<T> child, int weight)
        {
            if (weight < 1 || weight > 100)
                throw new ArgumentOutOfRangeException("The weight on a child queue is not between 1 to 100. ", (Exception?)null);

            lock (SyncObject)
            {
                CheckCorrectParent(child, optional: false);

                if (child.Weight == weight)
                    return;

                child.Weight = weight;

                EnsureBalancesRefilled(reset: true);
            }
        }

        /// <summary>
        /// Reset weights on many child queues at the same time.
        /// </summary>
        /// <remarks>
        /// Resetting weights requires re-calculation of internally
        /// cached data, so when many child queues need to have their
        /// weights reset, the changes should be batched together.
        /// </remarks>
        protected void ResetWeights(IEnumerable<KeyValuePair<SchedulingUnit<T>, int>> items)
        {
            // Defensive copy to avoid the values changing concurrently after
            // they are validated.
            var itemsArray = items.ToArray();

            foreach (var (_, weight) in itemsArray)
            {
                if (weight < 1 || weight > 100)
                    throw new ArgumentOutOfRangeException("The weight on a child queue is not between 1 to 100. ", (Exception?)null);
            }

            lock (SyncObject)
            {
                // Must check for correct parent only after locking first.
                foreach (var (child, _) in itemsArray)
                    CheckCorrectParent(child, optional: false);

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
        internal void ActivateChild(SchedulingUnit<T> child)
        {
            bool firstActivated = false;

            lock (SyncObject)
            {
                if (CheckCorrectParent(child, optional: true))
                    return;

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
            {
                OnFirstActivatedBase();
                OnFirstActivated();
            }
        }

        /// <summary>
        /// Called upon activating a child queue when there were
        /// previously no existing active child queues.
        /// </summary>
        private void OnFirstActivatedBase()
        {
            var toWakeUp = _toWakeUp;
            if (toWakeUp is SourceImpl sourceAdaptor)
                sourceAdaptor.OnSubgroupActivated();
            else if (toWakeUp is ChannelReader channelReader)
                channelReader.OnSubgroupActivated();
        }

        /// <summary>
        /// Called upon activating a child queue when there were
        /// previously no existing active child queues.
        /// </summary>
        /// <remarks>
        /// An event handler is not used for this purpose because
        /// it does not make sense for <see cref="SchedulingGroup{T}" />
        /// to be consumed from multiple clients.
        /// </remarks>
        protected virtual void OnFirstActivated()
        {
        }

        private void DeactivateChildCore(SchedulingUnit<T> child)
        {
            if (child.IsActive)
            {
                _priorityHeap.Delete(child.PriorityHeapIndex);
                UpdateForAverageBalance(child, child.Balance, 0);
            }
        }

        /// <summary>
        /// De-activate a child queue.
        /// </summary>
        /// <param name="child">
        /// The child scheduling unit.  This instance must be its parent.
        /// </param>
        internal void DeactivateChild(SchedulingUnit<T> child)
        {
            lock (SyncObject)
            {
                if (CheckCorrectParent(child, optional: true))
                    return;

                DeactivateChildCore(child);
            }
        }

        /// <summary>
        /// De-activate a child queue, and unset its field pointing to
        /// this parent.
        /// </summary>
        /// <remarks>
        /// This method is used to change a child queue's parent.
        /// </remarks>
        /// <param name="child">
        /// The child scheduling unit.  This instance must be its parent.
        /// </param>
        /// <param name="parentRef">
        /// Reference to the field <see cref="SchedulingUnit{T}._parent" />.
        /// It is nullified while locking the current (this) parent,
        /// so that <see cref="CheckCorrectParent" /> can reliably detect
        /// when the user attempted to change parents while executing
        /// another operation concurrently.
        /// </param>
        internal void DeactivateChildAndDisown(SchedulingUnit<T> child, 
                                               ref SchedulingGroup<T>? parentRef)
        {
            lock (SyncObject)
            {
                if (CheckCorrectParent(child, optional: true))
                    return;

                DeactivateChildCore(child);

                // Reset weight and statistics while holding the lock
                child.Weight = 1;
                child.Balance = 0;
                child.WasActivated = false;

                parentRef = null;
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
        internal void AdjustChildBalance(SchedulingUnit<T> child, int debit)
        {
            lock (SyncObject)
            {
                if (CheckCorrectParent(child, optional: true))
                    return;

                int oldBalance = child.Balance;
                int newBalance = MiscArithmetic.SaturatingAdd(oldBalance, debit);

                if (child.IsActive)
                {
                    _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);
                    UpdateForAverageBalance(child, oldBalance, newBalance);
                }

                child.Balance = newBalance;
            }

            if (_toWakeUp is SourceImpl sourceAdaptor)
                sourceAdaptor.OnAdjustBalance(debit);
        }

        /// <summary>
        /// Ensure that the child queue still belongs to this parent,
        /// called as part of double-checked locking.
        /// </summary>
        /// <remarks>
        /// The locking protocol for changing parents on a scheduling
        /// source ensures that the child queue points to this
        /// instance for the duration of the lock on <see cref="SyncObject" />,
        /// once this method checks successfully for that condition 
        /// initially upon taking the lock.
        /// </remarks>
        private bool CheckCorrectParent(SchedulingUnit<T> child, bool optional)
        {
            var parent = child.Parent;
            if (!object.ReferenceEquals(parent, this))
            {
                if (parent is null && optional)
                    return false;

                throw new InvalidOperationException(
                    "This operation on a scheduling source cannot be " +
                    "completed because its parent scheduling group was " +
                    "changed from another thread. ");
            }

            return true;
        }

        /// <summary>
        /// Admit a child queue to be managed by this scheduling group.
        /// </summary>
        /// <param name="child">
        /// The child queue to admit.  It must not have any other parent currently.
        /// </param>
        /// <remarks>
        /// The child queue will start off as inactive for this
        /// scheduling group.
        /// </remarks>
        protected void AdmitChild(SchedulingUnit<T> child)
        {
            if (child is SourceImpl sourceAdaptor &&
                object.ReferenceEquals(sourceAdaptor.Subgroup, this))
            {
                throw new InvalidOperationException("Cannot add a scheduling subgroup as a child of itself. ");
            }

            child.SetParent(this);
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
        private void UpdateForAverageBalance(SchedulingUnit<T> child, 
                                             int oldBalance, 
                                             int newBalance)
        {
            static long GetUnweightedBalance(SchedulingUnit<T> child, int balance)
                => ((long)balance * child.ReciprocalWeight) 
                    >> SchedulingUnit<T>.ReciprocalWeightLogScale;

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
        /// same as <see cref="SchedulingUnit{T}.Weight" />
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
