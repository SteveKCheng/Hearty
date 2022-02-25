using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hearty.Scheduling
{
    /// <summary>
    /// Distributes the costs for a job onto multiple scheduling accounts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class does accounting for the costs of a job that is 
    /// shared between multiple "participants" that want the job executed.  
    /// One instance shall be created for each shared job; 
    /// instances cannot be re-used for new jobs.
    /// </para>
    /// <para>
    /// When a job is not shared, its execution time, or any 
    /// other kind of abstract cost, can be recorded and updated
    /// on a designated <see cref="ISchedulingAccount" /> object.
    /// When there are multiple participants, each participant
    /// will have its own <see cref="ISchedulingAccount" />.
    /// On receiving updates through its own 
    /// <see cref="ISchedulingAccount" /> interface, an instance
    /// of this class will equally split up the costs and
    /// propagate them down to the participants' 
    /// <see cref="ISchedulingAccount" /> objects.
    /// </para>
    /// <para>
    /// Job sharing is expected to be a relatively rare occurrence,
    /// so this class is designed to be easy, straightforward
    /// and obviously correct, than to be clever to achieve
    /// extreme high performance.  Updates always lock the
    /// object's state as a whole.
    /// </para>
    /// </remarks>
    /// <typeparam name="TRegistration">
    /// Type of data that can be associated to each participant,
    /// which is disposed when the participant is removed.
    /// This class does not look into the data in any way,
    /// but offers a place to store it, since otherwise the
    /// user of this class would have to create a parallel
    /// structure and keep it synchronized with the list
    /// of participants stored here.
    /// </typeparam>
    internal sealed class SchedulingAccountSplitter<TRegistration> : ISchedulingAccount
    {
        SchedulingStatistics ISchedulingAccount.CompletionStatistics
            => throw new NotSupportedException();

        /// <summary>
        /// Completes the recording of charges for the job represented
        /// by this instance.
        /// </summary>
        /// <param name="charge">
        /// The completed charge for the job as a whole to record.
        /// All remaining participants will receive a call to
        /// <see cref="ISchedulingAccount.TabulateCompletedItem(int)" />
        /// with this charge split equally among them.
        /// </param>
        public void TabulateCompletedItem(int charge)
        {
            lock (_participants)
            {
                if (_isCompleted)
                    return;

                if (_participants.Count > 0)
                {
                    var participants = CollectionsMarshal.AsSpan(_participants);
                    int splitCharge = charge / participants.Length;
                    for (int i = 0; i < participants.Length; ++i)
                    {
                        ref var p = ref participants[i];
                        FinalizeParticipant(ref p);
                        p.Account.TabulateCompletedItem(splitCharge);
                    }

                    _participants.Clear();
                }

                _isCompleted = true;
            }
        }

        /// <summary>
        /// Charge a cost to the job which is split to all current
        /// participants.
        /// </summary>
        /// <param name="current">
        /// Set to the current running cost for the job if this
        /// call is updating the cost of the job.  (It should 
        /// almost always be the same value as what has been 
        /// recorded internally in this instance.) 
        /// Set to null when the job is about to start executing:
        /// the running cost will start at zero and 
        /// there will be an initializing call to 
        /// <see cref="ISchedulingAccount.UpdateCurrentItem" />
        /// on already registered participants.
        /// </param>
        /// <param name="change">
        /// If <paramref name="current" /> is non-null,
        /// this value is the change to the running cost.
        /// Otherwise this value is the initial deposit.
        /// </param>
        public void UpdateCurrentItem(int? current, int change)
        {
            lock (_participants)
            {
                if (_isCompleted)
                    return;

                if ((current != null) != _isStarted)
                {
                    throw new InvalidOperationException(
                        (current != null) ? "UpdateCurrentItem must be called first for initialization. "
                                          : "UpdateCurrentItem cannot be called more than once for initialization. ");
                }

                if (_isStarted)
                {
                    var currentValue = current.GetValueOrDefault();

                    if (_participants.Count > 0)
                    {
                        var participants = CollectionsMarshal.AsSpan(_participants);
                        int splitChange = (change + (currentValue - _runningCost))
                                        / participants.Length;

                        for (int i = 0; i < participants.Length; ++i)
                        {
                            ref var p = ref participants[i];

                            var newCharge = Math.Max(p.Deposit, p.RunningCost + splitChange);

                            int alreadyCharged = Math.Max(p.Deposit, p.RunningCost);
                            int adjustedChange = newCharge - alreadyCharged;

                            if (adjustedChange != 0)
                                p.Account.UpdateCurrentItem(alreadyCharged, adjustedChange);

                            p.RunningCost += splitChange;
                        }
                    }

                    _runningCost = currentValue + change;
                }
                else // !_isStarted
                {
                    // Distribute deposit equally among all participants.
                    if (_participants.Count > 0)
                    {
                        var participants = CollectionsMarshal.AsSpan(_participants);
                        int splitDeposit = Math.Max(change, 0) / participants.Length;

                        for (int i = 0; i < participants.Length; ++i)
                        {
                            ref var p = ref participants[i];
                            p.Deposit = splitDeposit;
                            p.Account.UpdateCurrentItem(null, splitDeposit);
                        }
                    }

                    _deposit = change;
                    _isStarted = true;
                }
            }
        }

        /// <summary>
        /// Change the over-charge / deposit for all participants.
        /// </summary>
        /// <param name="overcharge">
        /// The incremental charge over the running cost on each participant.
        /// As a contribution to <see cref="ISchedulingAccount.UpdateCurrentItem" />,
        /// this quantity is effectively floored to zero if it is negative.
        /// </param>
        /// <remarks>
        /// <para>
        /// <see cref="ISchedulingAccount.UpdateCurrentItem" /> on the participants
        /// is invoked to effect the new over-charge, unless <see cref="_isStarted" />
        /// is false.  
        /// </para>
        /// <para>
        /// This method must not be called when <see cref="_isStarted" /> is false.
        /// </para>
        /// </remarks>
        private void UpdateDeposits(int overcharge)
        {
            Debug.Assert(!_isStarted);

            var participants = CollectionsMarshal.AsSpan(_participants);
            int newOvercharge = Math.Max(overcharge, 0);

            for (int i = 0; i < participants.Length; ++i)
            {
                ref var p = ref participants[i];

                int oldOvercharge = Math.Max(p.Deposit - p.RunningCost, 0);

                int delta = newOvercharge - oldOvercharge;
                if (delta != 0)
                {
                    int alreadyCharged = Math.Max(p.Deposit, p.RunningCost);
                    p.Account.UpdateCurrentItem(alreadyCharged, delta);
                }

                p.Deposit = overcharge + p.RunningCost;
            }
        }

        /// <summary>
        /// Finalize charges on a participant and prepare for its removal.
        /// </summary>
        private void FinalizeParticipant(ref Participant participant)
        {
            if (_isStarted)
            {
                int delta = Math.Max(participant.Deposit - participant.RunningCost, 0);
                if (delta != 0)
                {
                    participant.Account.UpdateCurrentItem(participant.RunningCost + delta, -delta);
                    participant.Deposit = participant.RunningCost;
                }
            }

            _disposeFunc?.Invoke(_disposeFuncState, participant.Account, participant.Registration);
        }

        /// <summary>
        /// Add a scheduling account that will participate in
        /// sharing costs from the shared job.
        /// </summary>
        /// <param name="account">
        /// The scheduling account for the new participant.
        /// </param>
        /// <param name="registration">
        /// Arbitrary data to be associated with the participant
        /// that will be disposed when it is removed.
        /// </param>
        /// <returns>
        /// True if the participant has been added; false
        /// if it has not been because this instance has been
        /// completed.
        /// </returns>
        public bool AddParticipant(ISchedulingAccount account, 
                                   TRegistration registration)
        {
            lock (_participants)
            {
                if (_isCompleted)
                    return false;

                var count = _participants.Count + 1;

                // Get the amount of deposit that has not been used up
                // by the running cost, and distribute it equally among
                // all participants.
                //
                // If _isStarted is false, this quantity is always zero.
                int depositRemaining = Math.Max(_deposit - _runningCost, 0) / count;

                if (_isStarted)
                {
                    // Update for the smaller amount of deposit required
                    // among existing participants
                    UpdateDeposits(depositRemaining);

                    account.UpdateCurrentItem(null, depositRemaining);
                }

                // Add new participant taking a share of the deposit
                var p = new Participant(depositRemaining,
                                        account,
                                        registration);
                _participants.Add(p);
            }

            return true;
        }

        /// <summary>
        /// Remove zero or more participants and finalize charges on them,
        /// that match the specified account reference.
        /// </summary>
        /// <param name="account">
        /// Specifies the participants to remove by their account reference.
        /// There may be more than one participant as the same account
        /// reference can be added multiple times, although that is not
        /// an expected use of this class.
        /// </param>
        /// <returns>
        /// The number of participants that have accounts that are
        /// exactly the same object as <paramref name="account" />,
        /// and have been removed.
        /// </returns>
        public int RemoveParticipants(ISchedulingAccount account)
        {
            return RemoveParticipants(
                    account,
                    static (a, b, _) => object.ReferenceEquals(a, b));
        }

        /// <summary>
        /// Remove zero or more participants and finalize charges on them,
        /// according to a predicate.
        /// </summary>
        /// <param name="state">
        /// Arbitrary state that is passed to each invocation of
        /// <paramref name="predicate" />.
        /// </param>
        /// <param name="predicate">
        /// Predicate that determines which participants to remove.
        /// For each participant in this instance, the predicate is
        /// passed the "key" and "value" from <see cref="AddParticipant" />.
        /// The predicate shall return true if the participant should 
        /// be removed, false otherwise.
        /// </param>
        /// <returns>
        /// The number of participants that match the predicate 
        /// and have been removed.
        /// </returns>
        public int RemoveParticipants<TState>(TState state,
            Func<TState, ISchedulingAccount, TRegistration, bool> predicate)
        {
            lock (_participants)
            {
                int countRemoved = 0;
                int index = 0;
                while (index < _participants.Count)
                {
                    var p = _participants[index];
                    bool toRemove = predicate.Invoke(state, p.Account, p.Registration);
                    if (toRemove)
                    {
                        FinalizeParticipant(ref p);

                        // Swap in item from end to remove current one,
                        // to avoid quadratic running time
                        int endIndex = _participants.Count - 1;
                        var endItem = _participants[endIndex];
                        _participants.RemoveAt(endIndex);
                        if (index != endIndex)
                        {
                            _participants[index] = endItem;
                            ++countRemoved;
                        }

                        continue;
                    }

                    ++index;
                }

                // Adjust required deposits on the remaining participants
                if (index > 0 && countRemoved > 0 && _isStarted)
                {
                    int depositRemaining = Math.Max(_deposit - _runningCost, 0) 
                                         / index;
                    UpdateDeposits(depositRemaining);
                }

                return countRemoved;
            }
        }

        /// <summary>
        /// The current list of participants, in no particular order.
        /// </summary>
        /// <remarks>
        /// This member object is locked before any state 
        /// is consulted or manipulated.
        /// </remarks>
        private readonly List<Participant> _participants = new(capacity: 4);

        /// <summary>
        /// The initial charge on the job when it started, 
        /// as returned by <see cref="ISchedulingExpense.GetInitialCharge" />.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Although the running cost that should be charged
        /// to the job can be, and is expected to be, continually
        /// updated, an initial estimate of the total cost
        /// is still necessary so that the balance from
        /// the scheduling flow decreases as soon as the job 
        /// has been de-queued, so that its parent scheduling
        /// group does not consecutively de-queue from the same
        /// flow until the running cost gets updated, which may
        /// only happen after a delay.
        /// </para>
        /// <para>
        /// The deposit is best understood as an incremental charge
        /// over the running cost, when it is positive.  
        /// This over-charge is what prevents the scheduling flow 
        /// from de-queuing its next jobs
        /// too aggressively while the current job is still not finished.
        /// The over-charge is the key quantity, but as it varies
        /// with the running cost stored in <see cref="_runningCost" />,
        /// it is not recorded directly, whereas the deposit is constant
        /// once a job has started.
        /// </para>
        /// </remarks>
        private int _deposit;

        /// <summary>
        /// Continually updated cumulative charge on this job,
        /// regardless of the initial deposit.
        /// </summary>
        /// <remarks>
        /// This quantity is always zero until the job has started.
        /// </remarks>
        private int _runningCost;

        /// <summary>
        /// True if the job has been completed by calling
        /// <see cref="TabulateCompletedItem(int)" />.
        /// </summary>
        private bool _isCompleted;

        /// <summary>
        /// True if the job has been registered as being started to
        /// <see cref="ISchedulingAccount.UpdateCurrentItem" />
        /// on current participants.
        /// </summary>
        /// <remarks>
        /// Participants added in the future will also have 
        /// the initial call to 
        /// <see cref="ISchedulingAccount.UpdateCurrentItem" />
        /// made.
        /// </remarks>
        private bool _isStarted;

        /// <summary>
        /// Function called on a participant entry when it is
        /// to be removed.
        /// </summary>
        private readonly Action<object?, ISchedulingAccount, TRegistration>? _disposeFunc;

        /// <summary>
        /// Arbitrary state object passed along to <see cref="_disposeFunc" />.
        /// </summary>
        private readonly object? _disposeFuncState;

        /// <summary>
        /// Prepare to account costs with an initially empty
        /// set of participants, and an unstarted job.
        /// </summary>
        /// <param name="disposeFunc">
        /// Function to be called on a participant entry when it is
        /// gets removed.
        /// </param>
        /// <param name="disposeFuncState">
        /// Arbitrary state object to pass along to <paramref name="disposeFunc" />.
        /// </param>
        public SchedulingAccountSplitter(
            Action<object?, ISchedulingAccount, TRegistration>? disposeFunc = null,
            object? disposeFuncState = null)
        {
            _disposeFunc = disposeFunc;
            _disposeFuncState = disposeFuncState;
        }

        /// <summary>
        /// Prepare to account costs on an already-started job
        /// with an initially one participant.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This constructor can be used to swap in an instance of
        /// this class in place of <see cref="ISchedulingAccount" />
        /// for one participant, when the second participant is to 
        /// be added and the job has already started.
        /// </para>
        /// <para>
        /// This constructor will not execute any interface calls on 
        /// <paramref name="account" />.  So it is safe to speculatively
        /// create an instance of this class with this constructor,
        /// as part of a compare-exchange loop.
        /// </para>
        /// <para>
        /// If the job has not started, then the parameterless
        /// constructor should used instead, followed by a
        /// call to <see cref="AddParticipant" /> to register
        /// the first participant.  That sequence of calls is also
        /// safe to speculatively execute.
        /// </para>
        /// </remarks>
        /// <param name="disposeFunc">
        /// Function to be called on a participant entry when it is
        /// gets removed.
        /// </param>
        /// <param name="disposeFuncState">
        /// Arbitrary state object to pass along to <paramref name="disposeFunc" />.
        /// </param>
        /// <param name="deposit">
        /// The deposit, or initial charge, registered for the job
        /// as in <see cref="ISchedulingExpense.GetInitialCharge" />.
        /// </param>
        /// <param name="runningCost">
        /// The cost which has already been attributed to the
        /// participant.
        /// </param>
        /// <param name="account">
        /// The scheduling account for the first participant.
        /// </param>
        /// <param name="registration">
        /// Arbitrary data to be associated with the first participant
        /// that will be disposed when it is removed.
        /// </param>
        public SchedulingAccountSplitter(
            int deposit, 
            int runningCost,
            ISchedulingAccount account,
            TRegistration registration,
            Action<object?, ISchedulingAccount, TRegistration>? disposeFunc = null,
            object? disposeFuncState = null)
            : this(disposeFunc, disposeFuncState)
        {
            _deposit = deposit;
            _runningCost = runningCost;
            _isStarted = true;

            var p = new Participant(deposit, account, registration)
            {
                RunningCost = runningCost
            };
            _participants.Add(p);
        }

        /// <summary>
        /// Charge-accounting details for a participant partaking in a shared job.
        /// </summary>
        private struct Participant
        {
            /// <summary>
            /// The deposit that is considered charged to this participant.
            /// </summary>
            /// <remarks>
            /// <para>
            /// The role of the "deposit" is as described in 
            /// <see cref="SchedulingAccountSplitter{TRegistration}._deposit" />.
            /// </para>
            /// <para>
            /// When there are multiple participants, the current 
            /// over-charge for the whole job shall be divided equally to them.
            /// Note that it is the over-charge that is split, and not the deposit
            /// (the sum of the running cost and the over-charge), to avoid 
            /// over-paying for past work that have already been done by
            /// previous participants.
            /// </para>
            /// <para>
            /// Since the running cost of participants may vary, so does the 
            /// over-charge.  In turn the per-participant deposit may vary
            /// (unlike the job's deposit), and so we need to track that
            /// quantity here.
            /// </para>
            /// </remarks>
            public int Deposit;

            /// <summary>
            /// The running cost of the job 
            /// that has been attributed to this participant.
            /// </summary>
            /// <remarks>
            /// <para>
            /// If a participant comes in but exits later before the job
            /// is finished, it is still considered to have paid a 
            /// proportion of the work.  The current job presumably 
            /// took away actual time from the former participant that it
            /// could have used to do other work, so the charges 
            /// accumulated on it are not reversed.
            /// </para>
            /// <para>
            /// By similar reasoning, a participant does not retroactively
            /// pay work time before it has joined a job.  So, the running
            /// cost attributed to each participant is not necessarily uniform
            /// across all participants: i.e. it is not simply the job's
            /// running cost divided by the current number of participants.
            /// The running cost for each participant is accumulated separately
            /// and depends on the previous history of participants.
            /// </para>
            /// </remarks>
            public int RunningCost;

            /// <summary>
            /// The scheduling account for the participant to post charges to.
            /// </summary>
            public readonly ISchedulingAccount Account;

            /// <summary>
            /// Arbitrary data that can be associated with a participant.
            /// </summary>
            /// <remarks>
            /// It will be disposed when the participant is removed.
            /// </remarks>
            public readonly TRegistration Registration;

            public Participant(int deposit,
                               ISchedulingAccount account,
                               TRegistration registration)
            {
                Deposit = deposit;
                RunningCost = 0;
                Account = account;
                Registration = registration;
            }
        }
    }
}
