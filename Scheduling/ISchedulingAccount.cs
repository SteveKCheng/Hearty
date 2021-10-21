using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Allows adjusting the "account balance" on a scheduling unit
    /// for fair scheduling.
    /// </summary>
    public interface ISchedulingAccount
    {
        /// <summary>
        /// Adjust the account balance of this scheduling unit to affect
        /// its priority in fair scheduling.
        /// </summary>
        /// <param name="debit">
        /// The amount to add to the balance.  At the level of this interface,
        /// this amount is an abstract charge, but actual implementations
        /// will decide the measurement units by convention.  A common
        /// example would be the elapsed time taken to execute a "job", 
        /// in milliseconds.
        /// </param>
        void AdjustBalance(int debit);
    }
}
