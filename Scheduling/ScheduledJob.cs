using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// A job that can be consumed from a queuing system once resources
    /// are available to execute it.
    /// </summary>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing the job.
    /// </typeparam>
    public struct ScheduledJob<TInput, TOutput> : ISchedulingExpense
    {
        private readonly SharedFuture<TInput, TOutput> _future;

        /// <inheritdoc cref="ISchedulingExpense.InitialCharge" />
        public int InitialCharge => _future.InitialCharge;

        internal ScheduledJob(SharedFuture<TInput, TOutput> future)
        {
            _future = future;
        }

        public TInput Input => _future.Input;

        public bool TryStartJob() => true;

        public void SetResult(TOutput output) => _future.SetResult(output);
    }
}
