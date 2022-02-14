using System;
using System.Collections.Generic;
using System.Threading;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Shares the charges for items on multiple scheduling accounts.
    /// </summary>
    /// <remarks>
    /// This implementation does not use lock-free techniques since it
    /// is quite difficult.  As most scheduling items are expected to 
    /// be not charged to multiple accounts, so these slow paths should
    /// be taken seldomly.
    /// </remarks>
    internal sealed class SchedulingAccountSplitter : ISchedulingAccount
    {
        SchedulingStatistics ISchedulingAccount.CompletionStatistics 
            => throw new NotImplementedException();

        void ISchedulingAccount.UpdateCurrentItem(int? current, int change)
        {
            lock (_members)
            {
                int count = _members.Count;

                if (count != 0 && change != 0)
                {
                    int oldCharge = _currentCharge / count;
                    int deltaCharge = (change + (current ?? 0) - _currentCharge) / count;
                    foreach (var member in _members)
                        member.Account.UpdateCurrentItem(oldCharge, deltaCharge);
                }

                _currentCharge = (current ?? 0) + change;
            }
        }

        void ISchedulingAccount.TabulateCompletedItem(int charge)
        {
            lock (_members)
            {
                if (_isCompleted)
                    return;

                _isCompleted = true;
                _completedCharge = charge;

                foreach (var member in _members)
                {
                    member.Account.TabulateCompletedItem(charge);
                    member.CancellationRegistration.Dispose();
                }
            }
        }

        /// <summary>
        /// Set of scheduling accounts that are to share costs for
        /// processing an item.
        /// </summary>
        private readonly List<(ISchedulingAccount Account, 
                               CancellationTokenRegistration CancellationRegistration)> _members;

        /// <summary>
        /// Charges so far that have been propagated out to member accounts.
        /// </summary>
        private int _currentCharge = 0;

        /// <summary>
        /// True when <see cref="ISchedulingAccount.TabulateCompletedItem" />
        /// has been called on this instance.
        /// </summary>
        private bool _isCompleted;

        /// <summary>
        /// The argument to <see cref="ISchedulingAccount.TabulateCompletedItem" />
        /// when it was first called.
        /// </summary>
        private int _completedCharge;

        /// <summary>
        /// Prepare to replace a single scheduling account with
        /// multiple ones that share charges.
        /// </summary>
        /// <param name="account">The original scheduling account. </param>
        /// <param name="currentCharge">
        /// What has been charged to the original scheduling account so far.
        /// </param>
        public SchedulingAccountSplitter(ISchedulingAccount account,
                                         int currentCharge,
                                         CancellationTokenRegistration cancellationRegistration)
        {
            _members = new ()
            {
                (account, cancellationRegistration)
            };

            _currentCharge = currentCharge;
        }

        /// <summary>
        /// Add a member to participate in sharing scheduling costs.
        /// </summary>
        /// <remarks>
        /// This method actively adjusts charges on existing accounts 
        /// as they will need to pay a smaller share as new accounts
        /// are added.
        /// </remarks>
        /// <param name="account">
        /// A scheduling account to add.
        /// </param>
        public void AddMember(ISchedulingAccount account, 
                              CancellationTokenRegistration cancellationRegistration)
        {
            lock (_members)
            {
                int count = _members.Count;
                int newCharge = _currentCharge / (count + 1);

                if (count != 0 && _currentCharge != 0)
                {
                    int oldCharge = _currentCharge / count;
                    int deltaCharge = newCharge - oldCharge;

                    foreach (var member in _members)
                        member.Account.UpdateCurrentItem(oldCharge, deltaCharge);
                }

                account.UpdateCurrentItem(null, newCharge);

                // If already completed, act as if the new account had
                // been present when TabulateCompletedItem on this instance
                // was first called.
                if (_isCompleted)
                {
                    account.TabulateCompletedItem(_completedCharge);

                    // Avoid leaking registrations when processing is already complete
                    cancellationRegistration.Dispose();
                }
                
                _members.Add((account, cancellationRegistration));
            }
        }
    }
}
