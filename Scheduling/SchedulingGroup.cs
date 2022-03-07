using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Hearty.Utilities;

namespace Hearty.Scheduling
{
    /// <summary>
    /// Credit-based scheduling from a set of <see cref="SchedulingFlow{T}" />.
    /// </summary>
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

        /// <summary>
        /// The "sender" object that will be passed as the first argument to
        /// <see cref="_eventHandler" />.
        /// </summary>
        private readonly object _eventSender;

        /// <summary>
        /// Callback that is invoked whenever a child queue activates or de-activates.
        /// </summary>
        /// <remarks>
        /// Such a callback can be used to expire child queues that become empty
        /// (after a period of time), i.e. remove their entries from other
        /// data structures in which they are registered.
        /// </remarks>
        private readonly EventHandler<SchedulingActivationEventArgs>? _eventHandler;

        /// <summary>
        /// The maximum number of active child queues that are allowed.
        /// </summary>
        protected readonly int MaxCapacity = 1024;

        /// <summary>
        /// Construct a parent scheduling group.
        /// </summary>
        /// <param name="capacity">
        /// The number of child queues to allocate for initially in 
        /// internal data structures.
        /// </param>
        public SchedulingGroup(int capacity)
            : this(capacity, null, null)
        {
        }

        /// <summary>
        /// Construct a parent scheduling group.
        /// </summary>
        /// <param name="capacity">
        /// The number of child queues to allocate for initially in 
        /// internal data structures.
        /// </param>
        /// <param name="eventHandler">
        /// If non-null, the specified event handler is invoked
        /// whenever a child queue of the new scheduling group 
        /// is activated or de-activated.
        /// </param>
        /// <param name="eventSender">
        /// The "sender" argument to pass to <paramref name="eventHandler" />.
        /// If null, the "sender" argument will refer to the new instance
        /// of this class being constructed.
        /// </param>
        public SchedulingGroup(int capacity, EventHandler<SchedulingActivationEventArgs>? eventHandler, object? eventSender)
        {
            if (capacity < 0 || capacity > MaxCapacity)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _priorityHeap = new IntPriorityHeap<SchedulingFlow<T>>(
                (ref SchedulingFlow<T> item, int index) => item.PriorityHeapIndex = index,
                capacity: capacity);

            _lastExtractedBalance = -1;

            SyncObject = new object();

            _eventSender = eventSender ?? this;
            _eventHandler = eventHandler;
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
        /// Incremented by one every time the callback is
        /// invoked for activating or de-activating a child flow.
        /// </summary>
        private uint _eventCounter = 0;

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
        public int BalanceRefillAmount
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
        /// If true, the balance of a child flow is not capped to
        /// the remaining flows.
        /// </summary>
        /// <remarks>
        /// This option should be enabled if the balances in the
        /// scheduling flows represent available resources 
        /// rather than time.
        /// </remarks>
        public bool RetainBalanceAfterReactivation { get; set; }

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
        private bool TryTakeItem([MaybeNullWhen(false)] out T item, 
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

                    var eventArgs = DeactivateChildCore(child, 
                                                        temporary: false, 
                                                        out bool lastDeactivated);

                    if (eventArgs != null || lastDeactivated)
                    {
                        // Do not invoke user's function inside the lock
                        Monitor.Exit(syncObject);
                        try
                        {
                            NotifyDeactivation(lastDeactivated);
                            InvokeEventHandler(eventArgs);
                        }
                        finally
                        {
                            Monitor.Enter(syncObject);
                        }
                    }
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
        public void ResetWeight(SchedulingFlow<T> child, int weight, bool reset)
        {
            if (weight < 1 || weight > 128)
                throw new ArgumentOutOfRangeException("The weight on a child queue is not between 1 to 100. ", (Exception?)null);

            lock (SyncObject)
            {
                CheckCorrectParent(child, optional: false);

                if (child.Weight != weight)
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
            if (RetainBalanceAfterReactivation)
                return int.MaxValue;

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
            SchedulingActivationEventArgs? eventArgs = default;

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
                    eventArgs = _eventHandler != null ? new(true, 
                                                            false,
                                                            unchecked(++_eventCounter), 
                                                            child.Attachment) 
                                                      : null;
                }
            }

            if (firstActivated)
            {
                var toWakeUp = _toWakeUp;
                if (toWakeUp is FlowImpl flowAdaptor)
                    flowAdaptor.OnSubgroupActivated();
                else if (toWakeUp is ChannelReader channelReader)
                    channelReader.OnSubgroupActivated();
            }

            InvokeEventHandler(eventArgs);
        }

        /// <summary>
        /// Common code to run when a child queue is to be de-activated.
        /// </summary>
        /// <returns>
        /// Arguments for the event handler to invoke for the de-activation event.
        /// This method does not invoke it directly as it must be done outside
        /// the lock for this instance's own data structures.  The return
        /// value is null if the event handler should not be invoked.
        /// </returns>
        private SchedulingActivationEventArgs? DeactivateChildCore(SchedulingFlow<T> child,
                                                                   bool temporary,
                                                                   out bool lastDeactivated)
        {
            if (child.IsActive)
            {
                _priorityHeap.Delete(child.PriorityHeapIndex);
                child.RefillEpoch = _countRefills;
                lastDeactivated = (_priorityHeap.Count == 0);
                return _eventHandler != null ? new(activated: false, 
                                                   temporary,
                                                   unchecked(++_eventCounter), 
                                                   child.Attachment) 
                                             : null;
            }

            lastDeactivated = false;
            return null;
        }

        /// <summary>
        /// De-activate a child queue.
        /// </summary>
        /// <param name="child">
        /// The child scheduling unit.  This instance must be its parent.
        /// </param>
        internal void DeactivateChild(SchedulingFlow<T> child, bool temporary)
        {
            SchedulingActivationEventArgs? eventArgs;
            bool lastDeactivated;

            lock (SyncObject)
            {
                if (!CheckCorrectParent(child, optional: true))
                    return;

                eventArgs = DeactivateChildCore(child, 
                                                temporary, 
                                                out lastDeactivated);
            }

            NotifyDeactivation(lastDeactivated);
            InvokeEventHandler(eventArgs);
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
            SchedulingActivationEventArgs? eventArgs;
            bool lastDeactivated;

            lock (SyncObject)
            {
                if (!CheckCorrectParent(child, optional: true))
                    return;

                eventArgs = DeactivateChildCore(child, 
                                                temporary: false, 
                                                out lastDeactivated);

                // Reset weight and statistics while holding the lock
                child.RefillEpoch = 0;
                child.Balance = int.MaxValue;
                child.WasActivated = false;
                child.Attachment = null;

                parentRef = null;
            }

            NotifyDeactivation(lastDeactivated);
            InvokeEventHandler(eventArgs);
        }

        /// <summary>
        /// Invoke the callback for activating or de-activating a child queue,
        /// if there is one.
        /// </summary>
        private void InvokeEventHandler(in SchedulingActivationEventArgs? eventArgs)
        {
            if (eventArgs != null)
                _eventHandler!.Invoke(_eventSender, eventArgs.GetValueOrDefault());
        }

        /// <summary>
        /// Notifies <see cref="FlowImpl" />, if it has been instantiated,
        /// when all child queues have de-activated.
        /// </summary>
        /// <param name="lastDeactivated">
        /// The output flag from <see cref="DeactivateChildCore" />,
        /// indicating if notification should occur.
        /// </param>
        private void NotifyDeactivation(bool lastDeactivated)
        {
            if (lastDeactivated && _toWakeUp is FlowImpl flowAdaptor)
                flowAdaptor.OnSubgroupDeactivated();
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

            if (_toWakeUp is FlowImpl flowAdaptor)
                flowAdaptor.OnAdjustBalance(debit);
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
        public void AdmitChild(SchedulingFlow<T> child, bool activate, object? attachment = null)
        {
            if (child is FlowImpl flowAdaptor &&
                object.ReferenceEquals(flowAdaptor.Subgroup, this))
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
