using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Adjusts the "account balance" and tracks statistics as
    /// items are processed from a scheduling flow.
    /// </summary>
    public interface ISchedulingAccount
    {
        /// <summary>
        /// Adjust the account balance of the scheduling flow to affect
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

        /// <summary>
        /// Register an item that was obtained from the scheduling flow
        /// as completed, with a final account of the charge it incurred.
        /// </summary>
        /// <remarks>
        /// This method updates the statistics that are returned by
        /// <see cref="GetCompletionStatistics" />.
        /// </remarks>
        /// <param name="charge">
        /// The cumulative charge for one of the items (typically a job)
        /// that was processed from the scheduling flow.
        /// </param>
        void TabulateCompletedItem(int charge);

        /// <summary>
        /// Get the running statistics on the items that have been
        /// processed from the scheduling flow.
        /// </summary>
        /// <remarks>
        /// <para>
        /// "Processed" refers not only to that the items being
        /// de-queued but also having been delivered as messages or 
        /// executed as jobs.
        /// </para>
        /// <para>
        /// These statistics can be used for monitoring, but do not 
        /// directly affect the priority or rate that the scheduling 
        /// flow is drained at.
        /// </para>
        /// </remarks>
        /// <returns>
        /// Current snapshot of the statistics.  
        /// </returns>
        SchedulingStatistics CompletionStatistics { get; }
    }
}
