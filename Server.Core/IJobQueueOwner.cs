using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// A formal owner of a queue in the job queuing system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Different owners within the same priority should 
    /// get equal shares of time in fair job scheduling. 
    /// </para>
    /// <para>
    /// How connecting clients or users map to formal owners 
    /// of queues is up to the application.  At the formal
    /// level, instances of this interface are only used as
    /// look-up keys to the set of queues owned the formal
    /// owner.
    /// </para>
    /// </remarks>
    public interface IJobQueueOwner : IEquatable<IJobQueueOwner>
    {
        /// <summary>
        /// The title or short text description of this owner, for display.
        /// </summary>
        string Title { get; }
    }
}
