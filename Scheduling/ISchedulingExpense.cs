using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Reports the initial charge for an abstract item in fair scheduling.
    /// </summary>
    public interface ISchedulingExpense
    {
        /// <summary>
        /// The initial charge for the item that comes out 
        /// from <see cref="SchedulingGroup{T}.TryTakeItem" />.
        /// </summary>
        public int InitialCharge { get; }
    }
}
