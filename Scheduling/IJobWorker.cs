using System;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents an abstract worker that can execute (queued) jobs.
    /// </summary>
    /// <typeparam name="TInput">
    /// The inputs to execute a job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing a job.
    /// </typeparam>
    public interface IJobWorker<in TInput, TOutput>
    {
        /// <summary>
        /// Execute a (de-queued) job.
        /// </summary>
        /// <param name="input">
        /// The input describing what to execute, which typically comes
        /// from a job message of type <see cref="ScheduledJob{TInput, TOutput}" />.
        /// </param>
        /// <param name="cancellationToken">
        /// Used to cancel the job.
        /// </param>
        /// <param name="executionId">
        /// An arbitrary integer, assigned by some convention, that may 
        /// distinguish the jobs executed by this worker.
        /// </param>
        /// <returns>
        /// The outputs from completing the job.
        /// </returns>
        ValueTask<TOutput> ExecuteJobAsync(uint executionId,
                                           TInput input,
                                           CancellationToken cancellationToken);

        /// <summary>
        /// Name that identifies this worker, for debugging and monitoring.
        /// </summary>
        string Name { get; }
    }
}
