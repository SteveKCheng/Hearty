using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using JobBank.Utilities;

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
        private IntPriorityHeap<SchedulingFlow<T>> _priorityHeap;

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

            _priorityHeap = new IntPriorityHeap<SchedulingFlow<T>>(
                (ref SchedulingFlow<T> item, int index) => item.PriorityHeapIndex = index,
                capacity: capacity);

            _lastExtractedBalance = -1;

            SyncObject = new object();
        }

        /// <summary>
        /// Get the number of child queues managed by this instance
        /// that are currently active.
        /// </summary>
        protected int CountActiveSources => _priorityHeap.Count;

        /// <summary>
        /// Backing field for <see cref="BalanceRefillAmount" />.
        /// </summary>
        private int _balanceRefillAmount = (1 << 30);

        /// <summary>
        /// Count of number of balance refills performed.
        /// </summary>
        /// <remarks>
        /// This statistic needs to be maintained so that inactive
        /// queues that are re-activated after re-fills can 
        /// "catch up".
        /// </remarks>
        private uint _countRefills = 0;

        /// <summary>
        /// The balance of the last queue that was extracted
        /// as the maximum of the priority heap.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This statistic needs to maintained only for resetting
        /// the balance of a new child queue that is admitted
        /// when there are no other active queues.  This value
        /// should be reasonable in that the new queue should not
        /// jump far ahead of its sibling queues should they 
        /// be activated again. 
        /// </para>
        /// <para>
        /// This member takes on positive values, or the special
        /// value -1 meaning that it should be substituted with
        /// the current value of <see cref="_balanceRefillAmount" />.
        /// </para>
        /// </remarks>
        private int _lastExtractedBalance;

        /// <summary>
        /// The amount that queue balances get refilled by when they all
        /// become negative.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In theory, this quantity does not affect scheduling behavior:
        /// the queue debit balances could just decrease without bound. 
        /// Practically however, the queue balances must be prevented
        /// from overflowing the capacity of the (32-bit) integer.
        /// And so when all queue balances become negative, all the balances
        /// must increase by some positive amount to keep them within
        /// range.  This quantity sets the amount to add on top of
        /// bringing at least one queue's balance to zero.
        /// </para>
        /// <para>
        /// For normal use, this quantity should be set very high:
        /// the default is 2^30, which is also the maximum.  
        /// To aid in debugging, it may be set lower.  The minimum 
        /// allowed value is 2^7.
        /// </para>
        /// </remarks>
        protected int BalanceRefillAmount
        {
            get => _balanceRefillAmount;
            set
            {
                if (value < 128 || value > (1 << 30))
                    throw new ArgumentOutOfRangeException(nameof(value), "BalanceRefillAmount must be between 2^7 and 2^30 (inclusive). ");

                _balanceRefillAmount = value;
            }
        }

        /// <summary>
        /// Re-fill balances on all child queues when none are eligible
        /// to be scheduled.
        /// </summary>
        /// <returns>
        /// Whether there is at least one active child queue
        /// (after re-filling).
        /// </returns>
        private bool EnsureBalancesRefilled()
        {
            if (_priorityHeap.IsEmpty)
                return false;

            int best = _priorityHeap.PeekMaximum().Key;
            if (best > 0)
                return true;

            uint refillAmount = (uint)_balanceRefillAmount;
            uint refillTimes = ((uint)-best) / refillAmount + 1;
            long refillTotal = (int)refillTimes * (long)refillAmount;

            var arg = (this, refillTotal);
            _priorityHeap.Initialize(_priorityHeap.Count, ref arg,
                static (ref (SchedulingGroup<T> self, long refillTotal) passedArg, 
                        in Span<int> keys,
                        in Span<SchedulingFlow<T>> values) =>
                {
                    var (self, refillTotal) = passedArg;

                    for (int i = 0; i < keys.Length; ++i)
                    {
                        var child = values[i];
                        int oldBalance = child.Balance;

                        // Brings the "best" child queue to have a zero balance,
                        // while everything else remains zero or negative.
                        int newBalance = MiscArithmetic.SaturateToInt(
                                            (long)child.Balance + refillTotal);

                        child.Balance = newBalance;
                        keys[i] = newBalance;
                    }

                    return keys.Length;
                });

            _lastExtractedBalance = MiscArithmetic.SaturateToInt(best + refillTotal);
            unchecked { _countRefills += refillTimes; }

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
                while (EnsureBalancesRefilled())
                {
                    var entry = _priorityHeap.PeekMaximum();
                    var child = entry.Value;
                    _lastExtractedBalance = entry.Key;

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

                        long deflatedCharge = child.ComputeDeflatedCharge(charge);
                        int newBalance = MiscArithmetic.SaturateToInt(oldBalance - deflatedCharge);
                        child.Balance = newBalance;

                        if (child.IsActive)
                            _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);

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
        /// <param name="child">The child queue to change weights for. </param>
        /// <param name="weight">The new desired weight; must be between
        /// 1 and 128, inclusive. </param>
        /// <param name="reset">If true, the existing balance on the child
        /// queue is ignored, and reset so that the queue becomes available
        /// for de-queuing soon.
        /// </param>
        protected void ResetWeight(SchedulingFlow<T> child, int weight, bool reset)
        {
            if (weight < 1 || weight > 128)
                throw new ArgumentOutOfRangeException("The weight on a child queue is not between 1 to 100. ", (Exception?)null);

            lock (SyncObject)
            {
                CheckCorrectParent(child, optional: false);

                if (child.Weight == weight)
                    return;

                child.Weight = weight;

                if (reset)
                {
                    if (child.IsActive)
                    {
                        int newBalance = GetUpperBoundBalance();
                        child.Balance = newBalance;
                        _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);
                    }
                    else
                    {
                        child.RefillEpoch = 0;
                        child.Balance = int.MaxValue;
                    }
                }
            }
        }

        /// <summary>
        /// Get the maximum balance that a newly inserted 
        /// child queue is allowed to attain.
        /// </summary>
        /// <remarks>
        /// This cap on balances prevents a child queue from 
        /// "saving" up credits that it fails to take advantage
        /// of in the past.  
        /// </remarks>
        private int GetUpperBoundBalance()
        {
            return _priorityHeap.IsNonEmpty ? _priorityHeap[0].Key :
                   _lastExtractedBalance > 0 ? _lastExtractedBalance
                                             : _balanceRefillAmount;
        }

        /// <summary>
        /// Ensure a child is marked active and put it into
        /// the priority heap if it has positive balance.
        /// </summary>
        /// <param name="child">
        /// The child scheduling unit.  This instance
        /// must be its parent.
        /// </param>
        internal void ActivateChild(SchedulingFlow<T> child)
        {
            bool firstActivated = false;

            lock (SyncObject)
            {
                if (!CheckCorrectParent(child, optional: true))
                    return;
                
                child.WasActivated = true;

                if (!child.IsActive)
                {
                    // Calculate how much refill was missed, capping to prevent overflow
                    long refillTotal = unchecked((long)(_countRefills - child.RefillEpoch)
                                                    * _balanceRefillAmount);
                    refillTotal = (ulong)refillTotal <= (ulong)int.MaxValue 
                                    ? refillTotal : int.MaxValue;

                    int newBalance = MiscArithmetic.SaturateToInt(child.Balance + refillTotal);

                    // Do not allow the child queue to "save" up balances
                    // when it is idle: it essentially "loses its spot"
                    // if it de-activates with an over-balance remaining.
                    //
                    // However, if the child has been "busy" working on
                    // already de-queued jobs, the charges from those jobs
                    // will stay.  Note that SchedulingFlow<T>.AdjustBalance
                    // will still have an effect when the child is inactive.
                    int maxBalance = GetUpperBoundBalance();
                    newBalance = Math.Min(newBalance, maxBalance);

                    child.Balance = newBalance;
                    _priorityHeap.Insert(newBalance, child);

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

        private void DeactivateChildCore(SchedulingFlow<T> child)
        {
            if (child.IsActive)
            {
                _priorityHeap.Delete(child.PriorityHeapIndex);
                child.RefillEpoch = _countRefills;
            }
        }

        /// <summary>
        /// De-activate a child queue.
        /// </summary>
        /// <param name="child">
        /// The child scheduling unit.  This instance must be its parent.
        /// </param>
        internal void DeactivateChild(SchedulingFlow<T> child)
        {
            lock (SyncObject)
            {
                if (!CheckCorrectParent(child, optional: true))
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
        /// Reference to the field <see cref="SchedulingFlow{T}._parent" />.
        /// It is nullified while locking the current (this) parent,
        /// so that <see cref="CheckCorrectParent" /> can reliably detect
        /// when the user attempted to change parents while executing
        /// another operation concurrently.
        /// </param>
        internal void DeactivateChildAndDisown(SchedulingFlow<T> child, 
                                               ref SchedulingGroup<T>? parentRef)
        {
            lock (SyncObject)
            {
                if (!CheckCorrectParent(child, optional: true))
                    return;

                DeactivateChildCore(child);

                // Reset weight and statistics while holding the lock
                child.RefillEpoch = 0;
                child.Balance = int.MaxValue;
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
        internal void AdjustChildBalance(SchedulingFlow<T> child, int debit)
        {
            lock (SyncObject)
            {
                if (!CheckCorrectParent(child, optional: true))
                    return;

                int oldBalance = child.Balance;
                long deflatedDebit = child.ComputeDeflatedCharge(debit);
                int newBalance = MiscArithmetic.SaturateToInt(
                                    (long)oldBalance + deflatedDebit);
                child.Balance = newBalance;

                if (child.IsActive)
                    _priorityHeap.ChangeKey(child.PriorityHeapIndex, newBalance);
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
        private bool CheckCorrectParent(SchedulingFlow<T> child, bool optional)
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
        /// <param name="activate">
        /// Whether to activate the child queue immediately.
        /// </param>
        /// <param name="attachment">
        /// Reference to an arbitrary object that can be attached
        /// to <paramref name="child" />, which gets reported in
        /// callbacks for activation and de-activation.
        /// </param>
        /// <remarks>
        /// The child queue will start off as inactive for this
        /// scheduling group.
        /// </remarks>
        protected void AdmitChild(SchedulingFlow<T> child, bool activate, object? attachment = null)
        {
            if (child is SourceImpl sourceAdaptor &&
                object.ReferenceEquals(sourceAdaptor.Subgroup, this))
            {
                throw new InvalidOperationException("Cannot add a scheduling subgroup as a child of itself. ");
            }

            child.SetParent(this);

            child.Attachment = attachment;

            if (activate)
                ActivateChild(child);
        }
    }
}
