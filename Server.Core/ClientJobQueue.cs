using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using Hearty.Scheduling;

namespace Hearty.Server;

/// <summary>
/// Queue for job messages submitted by one client, to take
/// part in <see cref="JobsManager" />.
/// </summary>
public class ClientJobQueue
    : ISchedulingFlow<ILaunchableJob<PromisedWork, PromiseData>>
{
    private readonly SchedulingQueue<ILaunchableJob<PromisedWork, PromiseData>>
        _flow = new();

    private readonly CancellationTokenSource _cancellationSource = new();

    SchedulingFlow<ILaunchableJob<PromisedWork, PromiseData>> 
        ISchedulingFlow<ILaunchableJob<PromisedWork, PromiseData>>.AsFlow()
        => _flow;

    internal ClientJobQueue()
    {
    }

    /// <summary>
    /// Get the scheduling account associated with this queue.
    /// </summary>
    /// <remarks>
    /// This method is private because <see cref="ISchedulingAccount" /> 
    /// contains mutating methods that should only be called by
    /// the framework.
    /// </remarks>
    internal ISchedulingAccount SchedulingAccount => _flow;

    /// <summary>
    /// Enqueue a single job to the back of the queue.
    /// </summary>
    /// <param name="job">
    /// The (micro) job to enqueue.
    /// </param>
    public void Enqueue(ILaunchableJob<PromisedWork, PromiseData> job)
        => _flow.Enqueue(job);

    /// <summary>
    /// Enqueue a "macro" job to the back of the queue that
    /// will generate a series of "micro" jobs.
    /// </summary>
    /// <param name="jobs">
    /// The sequence of jobs (which make up the "macro job") to enqueue.
    /// </param>
    public void Enqueue(IAsyncEnumerable<ILaunchableJob<PromisedWork, PromiseData>> jobs)
        => _flow.Enqueue(jobs);

    /// <summary>
    /// Cancellation token associated with this queue.
    /// </summary>
    public CancellationToken CancellationToken => _cancellationSource.Token;

    /// <inheritdoc cref="ISchedulingAccount.CompletionStatistics" />
    public SchedulingStatistics CompletionStatistics => _flow.CompletionStatistics;

    /// <summary>
    /// Get the number of items currently in the queue.
    /// </summary>
    public int Count => _flow.Count;

    /// <summary>
    /// Credentials for the owner of this queue.
    /// </summary>
    /// <remarks>
    /// This information is only used for (administrative) display
    /// and does not affect the mechanics of queuing.
    /// </remarks>
    public ClaimsPrincipal? OwnerPrincipal { get; set; }
}
