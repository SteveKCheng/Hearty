using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents a worker that participates in a job distribution system.
    /// </summary>
    /// <remarks>
    /// This interface is not for submitting jobs, which must go through
    /// the job distribution system obviously.  It is only for monitoring.
    /// </remarks>
    public interface IDistributedWorker
    {
        /// <summary>
        /// Name that identifies this worker, for debugging and monitoring.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Get the count of total abstract resources.
        /// </summary>
        /// <remarks>
        /// Concretely this value is usually the count of CPUs
        /// that this worker has.
        /// </remarks>
        int TotalResources { get; }

        /// <summary>
        /// Get the count of abstract resources that are available
        /// to claim by new jobs.
        /// </summary>
        int AvailableResources { get; }
    }

    /// <summary>
    /// Extends <see cref="IDistributedWorker" /> to be able to view
    /// individual jobs being currently processed.
    /// </summary>
    /// <typeparam name="TInput">
    /// The inputs to execute a job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing a job.
    /// When a job is in progress, the output is not available, but if the 
    /// objects from <see cref="CurrentJobs" /> are retained 
    /// (e.g. in a monitoring user interface) past completion 
    /// then the outputs will be available.
    /// </typeparam>
    public interface IDistributedWorker<TInput, TOutput> : IDistributedWorker
    {
        /// <summary>
        /// Get a snapshot of the current jobs being currently processed.
        /// </summary>
        IEnumerable<SharedFuture<TInput, TOutput>> CurrentJobs { get; }
    }
}
