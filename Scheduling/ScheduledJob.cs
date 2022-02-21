using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents a job that can be consumed from a queuing system once resources
    /// are available to execute it.
    /// </summary>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing the job.
    /// </typeparam>
    /// <remarks>
    /// The job that is eventually executed is represented by 
    /// <see cref="SharedFuture{TInput, TOutput}" />, but the actual item
    /// to put into the queuing system is this structure which wraps
    /// <see cref="SharedFuture{TInput, TOutput}" />.  This wrapping allows
    /// the same job to be queued into more than one queue 
    /// (of differing priorities, such that the first to de-queue would
    /// launch the job), to avoid priority inversion.
    /// </remarks>
    public readonly struct ScheduledJob<TInput, TOutput> : ISchedulingExpense
    {
        /// <summary>
        /// The underlying "job" object that may be shared across multiple queues
        /// because it has been enqueued more than once.
        /// </summary>
        public ILaunchableJob<TInput, TOutput> Future { get; }
            
        /// <summary>
        /// The queue that has this instance enqueued, needed for adjusting
        /// its debit balance.
        /// </summary>
        public ISchedulingAccount Account { get; }

        /// <summary>
        /// Instantiates a representative of <see cref="SharedFuture{TInput, TOutput}" />.
        /// </summary>
        internal ScheduledJob(ILaunchableJob<TInput, TOutput> future,
                              ISchedulingAccount account)
        {
            Future = future;
            Account = account;
        }

        int ISchedulingExpense.GetInitialCharge() => Future.GetInitialCharge();
    }
}
