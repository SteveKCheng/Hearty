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
    public interface IJobWorker<TInput, TOutput>
    {
        /// <summary>
        /// Execute a (de-queued) job.
        /// </summary>
        /// <param name="executionId">
        /// An arbitrary integer, assigned by some convention, that may 
        /// distinguish the jobs executed by this worker.
        /// </param>
        /// <param name="future">
        /// The shared future that receives the result from this worker.
        /// The execution itself will need to refer to 
        /// <see cref="SharedFuture{TInput, TOutput}.Input" />,
        /// but the whole object is passed so that it can be retained
        /// for monitoring purposes.
        /// </param>
        /// <returns>
        /// The outputs from completing the job.
        /// </returns>
        ValueTask<TOutput> ExecuteJobAsync(uint executionId,
                                           SharedFuture<TInput, TOutput> future);

        /// <summary>
        /// Release reserved resources for a job
        /// when it is not going to be executed.
        /// </summary>
        /// <remarks>
        /// For each job (with reserved resources), there must be only one
        /// call to either this method or <see cref="ExecuteJobAsync" />.
        /// </remarks>
        /// <param name="executionId">
        /// An arbitrary integer, assigned by some convention, that may 
        /// distinguish the jobs executed by this worker.  This parameter
        /// should have the same value as would have been passed to
        /// <see cref="ExecuteJobAsync" /> has the job not been
        /// abandoned.
        /// </param>
        void AbandonJob(uint executionId);

        /// <summary>
        /// Name that identifies this worker, for debugging and monitoring.
        /// </summary>
        string Name { get; }
    }
}
