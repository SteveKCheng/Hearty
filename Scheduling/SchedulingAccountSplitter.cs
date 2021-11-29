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
                foreach (var member in _members)
                    member.Account.TabulateCompletedItem(charge);
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
        /// <returns>
        /// True if the account was added; false if it has already
        /// been registered.
        /// </returns>
        public bool TryAddMember(ISchedulingAccount account, 
                                 CancellationToken cancellationToken,
                                 Action<object?> cancellationAction,
                                 object? cancellationState)
        {
            lock (_members)
            {
                foreach (var member in _members)
                {
                    if (object.ReferenceEquals(member.Account, account))
                        return false;
                }

                int count = _members.Count;
                int newCharge = _currentCharge / (count + 1);

                if (count != 0 && _currentCharge != 0)
                {
                    int oldCharge = _currentCharge / count;
                    int deltaCharge = newCharge - oldCharge;

                    foreach (var member in _members)
                        account.UpdateCurrentItem(oldCharge, deltaCharge);
                }

                var registration = cancellationToken.Register(cancellationAction, 
                                                              cancellationState);

                _members.Add((account, registration));
                account.UpdateCurrentItem(null, newCharge);

                return true;
            }
        }

        /// <summary>
        /// Cancel registrations on all clients' <see cref="CancellationToken" />.
        /// </summary>
        public void DisposeCancellationRegistrations()
        {
            lock (_members)
            {
                foreach (var member in _members)
                    member.CancellationRegistration.Dispose();
            }
        }
    }
}
