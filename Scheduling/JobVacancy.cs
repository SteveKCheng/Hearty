using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents a claim to resources to execute a job in abstract
    /// job scheduling.
    /// </summary>
    /// <typeparam name="TInput">
    /// The inputs to execute a job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing a job.
    /// </typeparam>
    public readonly struct JobVacancy<TInput, TOutput>
    {
        /// <summary>
        /// An arbitrary ID that can be assigned by the originator
        /// of this instance, for debugging and monitoring.
        /// </summary>
        public uint ExecutionId { get; }

        private readonly Func<TInput, ValueTask<TOutput>> _action;

        /// <summary>
        /// Use this vacancy to launch a scheduled job,
        /// unless the job was already launched.
        /// </summary>
        /// <returns>
        /// True if the job has just been launched; false
        /// if it had already been launched earlier.
        /// </returns>
        public bool TryLaunchJob(in ScheduledJob<TInput, TOutput> job)
            => job.Future.TryLaunchJob(_action);

        /// <summary>
        /// Represent a claim to resources on a worker to launch a job.
        /// </summary>
        /// <param name="action"></param>
        /// <param name="executionId">
        /// See <see cref="ExecutionId" />.
        /// </param>
        public JobVacancy(Func<TInput, ValueTask<TOutput>> action, 
                          uint executionId)
        {
            _action = action;
            ExecutionId = executionId;
        }
    }
}
