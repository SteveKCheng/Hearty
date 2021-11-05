using System;
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
        public void LaunchJob(ScheduledJob<TInput, TOutput> job)
            => _action.Invoke(_state, job);

        /// <summary>
        /// An arbitrary ID that can be assigned by the originator
        /// of this instance, for debugging and monitoring.
        /// </summary>
        public uint ExecutionId { get; }

        private readonly object? _state;
        private readonly Action<object?, ScheduledJob<TInput, TOutput>> _action;

        public JobVacancy(Action<object?, ScheduledJob<TInput, TOutput>> action, 
                          object? state,
                          uint executionId)
        {
            _action = action;
            _state = state;
            ExecutionId = executionId;
        }
    }
}
