using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents a job currently being processed by a worker, for monitoring purposes.
    /// </summary>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    public interface IRunningJob<out TInput>
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

        /// <summary>
        /// The initial estimate of the amount of time the job
        /// would take, in milliseconds.
        /// </summary>
        int InitialWait { get; }
    }
}
