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
        /// <param name="executionId">
        /// An arbitrary integer, assigned by some convention, that may 
        /// distinguish the jobs executed by this worker.
        /// </param>
        /// <param name="runningJob">
        /// Holds an object that manages the job.
        /// The execution itself will need to refer to 
        /// <see cref="IRunningJob{TInput}.Input" />,
        /// but the whole object is passed so that it can be retained
        /// for monitoring purposes.
        /// </param>
        /// <param name="cancellationToken">
        /// Used to cancel the job.
        /// </param>
        /// <returns>
        /// The outputs from completing the job.
        /// </returns>
        ValueTask<TOutput> ExecuteJobAsync(uint executionId,
                                           IRunningJob<TInput> runningJob,
                                           CancellationToken cancellationToken);

        /// <summary>
        /// Release reserved resources for a job
        /// when it is not going to be executed.
        /// </summary>
        /// <remarks>
        /// For each job (with reserved resources), there must be only one
        /// call to either this method or <see cref="ExecuteJobAsync" />.
        /// This method essentially represents a job to execute 
        /// that is "null".
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

        /// <summary>
        /// Whether this worker is working normally and
        /// accepting jobs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If this worker represents a separate process, it may fail
        /// without the entire scheduling system failing. 
        /// When that happens the scheduling system should avoid
        /// submitting further jobs to the worker which would only
        /// fail.
        /// </para>
        /// <para>
        /// When this flag becomes false, the condition is 
        /// irreversible for the same object.  That is, the
        /// same object cannot be revived.  Doing so avoids 
        /// tricky and complicated coordination for revival
        /// between the object and and its consuming client, 
        /// whose implementations may execute concurrently.
        /// </para>
        /// <para>
        /// If this worker is detected to fail, this flag should be set to
        /// false before reporting the exception from any task returned
        /// by <see cref="ExecuteJobAsync" />.  Thus, a client can check
        /// for a failed worker by consulting this flag after awaiting
        /// the completion of <see cref="ExecuteJobAsync" />,
        /// without risk of false negatives. 
        /// </para>
        /// </remarks>
        bool IsAlive { get; }
    }
}
