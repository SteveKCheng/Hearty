using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Scheduling
{
    /// <summary>
    /// Statistics on the items from a scheduling flow.
    /// </summary>
    /// <remarks>
    /// These statistics are not required to be exactly up-to-date with 
    /// the current state of the scheduling flow they pertain to.
    /// To guarantee otherwise is impossible since there is no 
    /// transactional synchronization.  But the statistics should be 
    /// read together without tearing, i.e. the individual members 
    /// here should be consistent with each other.
    /// </remarks>
    public readonly struct SchedulingStatistics
    {
        /// <summary>
        /// The sum of all charges of the target items.
        /// </summary>
        public long CumulativeCharge { get; init; }

        /// <summary>
        /// The count of the target items.
        /// </summary>
        public int ItemsCount { get; init; }
    }
}
