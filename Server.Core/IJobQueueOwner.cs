using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Server
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
    /// owner.  Owners are not keyed with plain strings,
    /// for type safety, and to allow metadata about
    /// the owner to be hung off this object.
    /// </para>
    /// <para>
    /// This interface needs to implement <see cref="IComparable{T}" />
    /// to allow instances to be used as keys in sorted containers.
    /// </para>
    /// </remarks>
    public interface IJobQueueOwner : IComparable<IJobQueueOwner>
                                    , IEquatable<IJobQueueOwner>
    {
        /// <summary>
        /// The title or short text description of this owner, for display.
        /// </summary>
        string Title { get; }

        /// <summary>
        /// Optional user identity that may be established by
        /// a server-side framework such as ASP.NET Core.
        /// </summary>
        ClaimsPrincipal? Principal { get; }
    }
}
