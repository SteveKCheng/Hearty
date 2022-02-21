using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents a job currently being processed by a worker, 
    /// for monitoring purposes.
    /// </summary>
    public interface IRunningJob
    {
        /// <summary>
        /// The initial estimate of the amount of time the job
        /// would take, in milliseconds.
        /// </summary>
        int EstimatedWait { get; }

        /// <summary>
        /// The approximate time that the job was launched,
        /// measured in the same convention as <see cref="Environment.TickCount64" />.
        /// </summary>
        long LaunchStartTime { get; }
    }

    /// <summary>
    /// Represents a job currently being processed by a worker, 
    /// along with its input, for monitoring purposes.
    /// </summary>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    public interface IRunningJob<out TInput> : IRunningJob
    {
        /// <summary>
        /// The inputs to execute the job.
        /// </summary>
        /// <remarks>
        /// Depending on the application, this member may be some delegate to run, 
        /// if the job is implemented entirely in-process.  Or it may be declarative 
        /// data, that may be serialized to run the job on a remote computer.  
        /// </remarks>
        TInput Input { get; }
    }

    /// <summary>
    /// Represents a job that can be launched by a worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface abstracts out what is common between a normally
    /// enqueued job and a job that needs to be re-queued for retrying.
    /// </para>
    /// <para>
    /// It is assumed possible that the same "launchable job" may be
    /// queued multiple times.  But of course the exact same work
    /// should not be repeated if it has already started.  So 
    /// "launching" the job is one-shot, and all consumers read
    /// from the same task source object.  This design helps
    /// with thread safety but complicates matters when the job
    /// needs to be re-tried on failure. 
    /// </para>
    /// </remarks>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing the job.
    /// </typeparam>
    public interface ILaunchableJob<out TInput, TOutput> : IRunningJob<TInput>
                                                         , ISchedulingExpense
    {
        /// <summary>
        /// Have a worker launch this job, but only if it has not been
        /// launched before.
        /// </summary>
        /// <param name="worker">
        /// The worker that would execute the job.
        /// </param>
        /// <param name="executionId">
        /// ID to identify the job execution for the worker, assigned by convention.
        /// </param>
        /// <returns>
        /// True if this job has been launched on <paramref name="worker" />;
        /// false if it was already launched (on another worker).
        /// </returns>
        bool TryLaunchJob(IJobWorker<TInput, TOutput> worker,
                          uint executionId);

        /// <summary>
        /// Asynchronous task that furnishes the result
        /// when the job finishes executing.
        /// </summary>
        Task<TOutput> OutputTask { get; }
    }
}
