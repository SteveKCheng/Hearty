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
    }
}
