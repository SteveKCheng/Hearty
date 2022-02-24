using System.Threading;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace JobBank.Server.WebApi
{
    /// <summary>
    /// Allows remote APIs to cancel promised work without
    /// carrying references to cancellation sources.
    /// </summary>
    /// <typeparam name="T">
    /// Represents the target work to cancel, typically
    /// a serializable identifier.
    /// </typeparam>
    /// <remarks>
    /// <para>
    /// When a remote process schedules work in the current
    /// .NET process through an API, it obviously cannot hold
    /// instances of <see cref="CancellationTokenSource" /> 
    /// directly to trigger cancellations.  It only has an
    /// identifier or abstract reference to the work which 
    /// will have to be de-references from a look-up table.
    /// </para>
    /// <para>
    /// This interface provides an abstract interface 
    /// to do that look-up and cancellation.  It assumes
    /// that each cancellation may be need to be authorized,
    /// i.e. if an implementation of this interface is scoped
    /// to the entire server rather than to an already
    /// authorized connection.
    /// </para>
    /// <para>
    /// This interface was originally made for 
    /// <see cref="PromisesEndpoints" /> to be able to
    /// cancel promises obtain 
    /// from <see cref="JobsManager" />.  However,
    /// the latter is deliberately 
    /// not a dependency of the former because the user
    /// of the library should be able to completely
    /// customize job scheduling, or not even use
    /// <see cref="JobsManager" /> at all. 
    /// So cancellation needed to be abstracted away,
    /// along with any authorization policies.
    /// </para>
    /// </remarks>
    public interface IRemoteCancellation<T>
    {
        /// <summary>
        /// Attempt to cancel previously scheduled or 
        /// currently executing work.
        /// </summary>
        /// <param name="client">
        /// Describes the requester's credentials or claims
        /// to the work or to cancel the work.
        /// </param>
        /// <param name="target">
        /// Identifies the work to cancel.
        /// </param>
        /// <param name="force">
        /// If true, and the targeted work is shared by multiple
        /// users, then the work is to be cancelled for all users.
        /// If false, the targeted work may still be continue
        /// after this call finishes if other users still claim
        /// it without having cancelled.
        /// </param>
        /// <returns>
        /// Reports the status of the cancellation request.
        /// Note that the cancellation may be occurring in the
        /// background even after the returned task completes.
        /// The return task may asynchronously complete if 
        /// even accepting the request requires some asynchronous
        /// operation internally, e.g. looking up the targeted
        /// work in a database.
        /// </returns>
        ValueTask<CancellationStatus> TryCancelAsync(ClaimsPrincipal client, 
                                                     T target, 
                                                     bool force);
    }

    /// <summary>
    /// Reports what has been done with a request to cancel
    /// in <see cref="IRemoteCancellation{T}.TryCancelAsync" />.
    /// </summary>
    public enum CancellationStatus
    {
        /// <summary>
        /// The work to cancel is not found or no longer exists.
        /// </summary>
        NotFound,

        /// <summary>
        /// The work cannot be cancelled because it does not
        /// support cancellation.
        /// </summary>
        NotSupported,

        /// <summary>
        /// The requesting user does not have rights to cancel
        /// the work.
        /// </summary>
        Forbidden,

        /// <summary>
        /// Cancellation has been processed for the work.
        /// </summary>
        /// <remarks>
        /// The cancellation may happen asynchronously.
        /// </remarks>
        Cancelled
    }

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
