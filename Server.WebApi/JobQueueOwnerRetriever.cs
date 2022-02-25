using System.Security.Claims;
using System.Threading.Tasks;

namespace Hearty.Server.WebApi
{
    /// <summary>
    /// Maps credentials received from a remote client to an (abstract) owner
    /// of a job queue.
    /// </summary>
    /// <param name="principal">Represents the user identity of the remote client,
    /// if any. </param>
    /// <param name="id">
    /// An explicitly specifier of the queue owner from the remote client.
    /// If non-null, this argument should override any default
    /// queue owner implied by the passed-in <see cref="ClaimsPrincipal" />.
    /// </param>
    /// <returns>
    /// The abstract job queue owner if it exists.
    /// </returns>
    /// <remarks>
    /// An implementation of this delegate could authorize the specified owner 
    /// against <see cref="ClaimsPrincipal" />, but by convention it is not 
    /// expected to when used under the ASP.NET Core.  Web servers are recommended
    /// to use the well-developed authorization framework from ASP.NET Core,
    /// which already provides enormous flexibility and customization points.
    /// </remarks>
    public delegate ValueTask<IJobQueueOwner?>
        JobQueueOwnerRetriever(ClaimsPrincipal? principal, string? id);
}
