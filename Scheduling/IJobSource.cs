using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Abstraction for the fair job scheduling system to pull out
    /// one job to execute.
    /// </summary>
    public interface IJobSource
    {
        /// <summary>
        /// Provides one job to process.
        /// </summary>
        /// <param name="charge">The amount of credit to charge
        /// to the abstract queue where the job is coming from,
        /// to effect fair scheduling.
        /// </param>
        /// <remarks>
        /// If this method returns null, the abstract queue will be 
        /// temporarily de-activated by the caller, so the system
        /// avoid polling repeatedly for work.  No further
        /// calls to this method is made until re-activation.
        /// </remarks>
        /// <returns>
        /// The job that the fair job scheduling system should be
        /// processing next, or null if this source instance
        /// currently has no job to process.  
        /// </returns>
        ScheduledJob? TakeJob(out int charge);
    }
}
