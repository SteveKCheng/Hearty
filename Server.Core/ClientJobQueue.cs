using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using Hearty.Scheduling;

namespace Hearty.Server;

/// <summary>
/// A job tagged with the client queue that it came from.
/// </summary>
internal struct ClientJobMessage
{
    /// <summary>
    /// The client queue that the job had been enqueued into.
    /// </summary>
    public readonly ClientJobQueue Queue;

    /// <summary>
    /// The job to run on a worker.
    /// </summary>
    public readonly ILaunchableJob<PromisedWork, PromiseData> Job;

    /// <summary>
    /// Constructor.
    /// </summary>
    public ClientJobMessage(ClientJobQueue queue,
                            ILaunchableJob<PromisedWork, PromiseData> job)
    {
        Queue = queue;
        Job = job;
    }
}

/// <summary>
/// Queue for job messages submitted by one client, to take
/// part in <see cref="JobsManager" />.
/// </summary>
public class ClientJobQueue
    : ISchedulingFlow<ClientJobMessage>
    , IReadOnlyCollection<IPromisedWorkInfo>
{
    private readonly SchedulingQueue<ILaunchableJob<PromisedWork, PromiseData>,
                                     ClientJobMessage>
        _flow;

    private readonly CancellationTokenSource _cancellationSource = new();

    SchedulingFlow<ClientJobMessage> 
        ISchedulingFlow<ClientJobMessage>.AsFlow() => _flow;

    internal ClientJobQueue()
    {
        _flow = new(item => new ClientJobMessage(this, item));
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
    /// Gets a snapshot of the items in the queue in 
    /// summary form, for monitoring purposes.
    /// </summary>
    public IEnumerator<IPromisedWorkInfo> GetEnumerator()
    {
        using var source = _flow.GetEnumerator();

        while (source.MoveNext())
        {
            var item = source.Current;
            var macroJob = item.Multiple;
            if (macroJob is IPromisedWorkInfo info)
                yield return info;
            else if (macroJob is null)
                yield return item.Single.Input;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

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
