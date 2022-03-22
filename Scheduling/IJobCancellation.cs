using System;
using System.Threading;

namespace Hearty.Scheduling
{
    /// <summary>
    /// Interface to cancel a job on behalf of a client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET code typically cancels operations by 
    /// observing a passed-in <see cref="CancellationToken" />.  
    /// However, cancellation tokens ultimately have to come from 
    /// <see cref="CancellationTokenSource" /> objects.
    /// For server applications that need to honor requests 
    /// from remote clients, mapping
    /// the combination of the client's identity and the job
    /// back to <see cref="CancellationTokenSource" /> object that
    /// created the cancellation token can be very non-trivial. 
    /// </para>
    /// <para>
    /// Furthermore, if the same operation is being shared by
    /// multiple clients, the job must count the active clients,
    /// and then only terminate when all clients disengage.
    /// To make the last part work, the job must be able to precisely
    /// cancel its own work, which cannot be done when it is merely
    /// observing an arbitrary <see cref="CancellationToken" />.
    /// </para>
    /// <para>
    /// This interface offers an alternative: a job (operation)
    /// can be cancelled by passing in only an object representing 
    /// the client identity.  
    /// </para>
    /// <para>
    /// An implementation of this interface likely must manage its own
    /// <see cref="CancellationTokenSource" />.  The complexity
    /// does not disappear but at least it is pushed down to
    /// the implementation so that callers can be simplified.
    /// </para>
    /// <para>
    /// This interface makes more efficient server applications 
    /// that queue many jobs, in that any internally
    /// managed <see cref="CancellationTokenSource" /> may be 
    /// created lazily, only when a job gets de-queued.
    /// </para>
    /// <para>
    /// Cancellation through this interface, of course, can only
    /// be cooperative in the same way as <see cref="CancellationToken" />,
    /// but implementations suitable for a server should make
    /// efforts to ensure (runaway) jobs can be stopped.
    /// </para>
    /// </remarks>
    public interface IJobCancellation
    {
        /// <summary>
        /// Request cancellation of this job on behalf of a client.
        /// </summary>
        /// <remarks>
        /// If the job is shared by other clients, the job
        /// is not stopped until those clients also request
        /// cancellation.
        /// </remarks>
        /// <param name="clientToken">
        /// Represents the client that wants to drop interest
        /// in this job.  It is compared against the tokens
        /// that have been registered for clients in the
        /// receiver object.  Clients are keyed using a
        /// <see cref="CancellationToken" /> 
        /// since it can be reasonably expected to be created
        /// by a server application any, although the token 
        /// passed here may not necessarily be cancelled if
        /// the token represents all of a client's operations,
        /// but the client is only request that only this job 
        /// be cancelled.
        /// </param>
        /// <param name="background">
        /// If true, any potentially heavy processing of the
        /// cancellation request should occur in the background.
        /// If false, the processing should occur synchronously,
        /// if possible.  Server applications will want to set
        /// this argument to true to reduce the latency of the
        /// response back to the client, at the cost of using
        /// more resources on the server.  Typically an implementation
        /// would queue any internally registered callbacks for
        /// cancellation on a thread pool.
        /// </param>
        /// <returns>
        /// True if the given client had an interest in this job
        /// which has been cancelled.  False if the given client
        /// is not registered with this job, or the job has already
        /// been cancelled.
        /// </returns>
        bool CancelForClient(CancellationToken clientToken, bool background);

        /// <summary>
        /// Request termination of this job for all clients.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="CancelForClient" />, this method
        /// causes the job to terminate regardless of other
        /// clients.  It is typically invoked on behalf of
        /// administrators to stop undesired or runaway jobs
        /// on a server.
        /// </remarks>
        /// <param name="background">
        /// If true, any potentially heavy processing of the
        /// cancellation request should occur in the background.
        /// If false, the processing should occur synchronously,
        /// if possible.  Server applications will want to set
        /// this argument to true to reduce the latency of the
        /// response back to the client, at the cost of using
        /// more resources on the server.  Typically an implementation
        /// would queue any internally registered callbacks for
        /// cancellation on a thread pool.
        /// </param>
        void Kill(bool background);

        /// <summary>
        /// Whether the job has already been requested to be killed.
        /// </summary>
        /// <remarks>
        /// This property is useful for user interfaces to disable
        /// the control to kill a job when the request is already 
        /// being processed.
        /// </remarks>
        bool KillingHasBeenRequested { get; }
    }
}
