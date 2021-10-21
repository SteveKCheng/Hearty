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
        public ValueTask RunJobAsync(ScheduledJob<TInput, TOutput> job)
            => _action.Invoke(_state, job);

        private readonly object? _state;
        private readonly Func<object?, ScheduledJob<TInput, TOutput>, ValueTask> _action;

        public JobVacancy(Func<object?, ScheduledJob<TInput, TOutput>, ValueTask> action, object? state)
        {
            _action = action;
            _state = state;
        }
    }
}
