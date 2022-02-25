using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Server
{
    /// <summary>
    /// Allows remote APIs to cancel promised work without
    /// carrying references to cancellation sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When a remote process schedules work in the current
    /// .NET process through an API, it obviously cannot hold
    /// instances of <see cref="CancellationTokenSource" /> 
    /// directly to trigger cancellations.  It only has an
    /// identifier or abstract reference to the work which 
    /// will have to be de-references from a look-up table.
    /// This interface provides an abstract interface 
    /// to do that look-up and cancellation.  
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
    public interface IRemoteJobCancellation
    {
        /// <summary>
        /// Cancel a job for one client that scheduled it earlier.
        /// </summary>
        /// <param name="queueKey">
        /// Identifies the queue to remove and cancel the job from.
        /// </param>
        /// <param name="target">
        /// The ID of the promise associated to the job.
        /// </param>
        /// <returns>
        /// False if the combination of client and promise is no longer
        /// registered.  True if the combination is registered
        /// and cancellation has been requested.
        /// </returns>
        bool TryCancelJobForClient(JobQueueKey queueKey, PromiseId target);

        /// <summary>
        /// Forcibly kill a job for all clients.
        /// </summary>
        /// <param name="target">
        /// The ID of the promise associated to the job.
        /// </param>
        /// <returns>
        /// False if the job does not exist.  True if it exists
        /// and it has been requested to be killed.  
        /// </returns>
        /// <remarks>
        /// This method is intended to be used by administrators.
        /// The server framework that is the caller is responsible
        /// for authorizing the request.
        /// </remarks>
        bool TryKillJob(PromiseId target);
    }
}
