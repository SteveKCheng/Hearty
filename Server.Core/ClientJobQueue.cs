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
/// To be disposed when the job registered with
/// <see cref="ClientJobQueue.RegisterRunningJob" /> has finished running.
/// </summary>
public readonly struct RunningJobRegistration : IDisposable
{
    private readonly LinkedListNode<IRunningJob<PromisedWork>> _node;

    internal RunningJobRegistration(LinkedListNode<IRunningJob<PromisedWork>> node)
    {
        _node = node;
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        var node = _node;
        var list = node?.List;
        if (list is not null)
        {
            lock (list)
                list.Remove(node!);
        }
    }
}

/// <summary>
/// Queue for job messages submitted by one client, to take
/// part in <see cref="JobsManager" />.
/// </summary>
public class ClientJobQueue
    : ISchedulingFlow<ClientJobMessage>
    , IReadOnlyCollection<IRunningJob<PromisedWork>>
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
    /// Jobs registered by <see cref="RunningJobRegistration" />.
    /// </summary>
    /// <remarks>
    /// A linked list is used for its absolute O(1) running time 
    /// and that it preserves the order of insertion.
    /// </remarks>
    private readonly LinkedList<IRunningJob<PromisedWork>> _runningJobs = new();

    /// <summary>
    /// Registers a job for monitoring after it has been de-queued
    /// but is still running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="IReadOnlyCollection{T}" /> implementation made
    /// available by the queue itself (<see cref="_flow" />)
    /// will drop a job item once it has been de-queued.  But,
    /// when a user wants to see the jobs in a client's queue, 
    /// naturally the jobs that are still running should be included.
    /// </para>
    /// <para>
    /// To that end, running jobs are registered into here after
    /// they have been de-queued.  The list of registered jobs
    /// is only used for user-level monitoring.  
    /// </para>
    /// <para>
    /// Normally, the same job should not be enqueued more than
    /// once.  But this method counts the number of times it
    /// is called for the same job (same object reference), and 
    /// it must be unregistered
    /// that same number of items to be removed from the list
    /// of current jobs.
    /// </para>
    /// <para>
    /// There can be the window between
    /// when the job is de-queued and when it is registered,
    /// so that it will not appear in the list returned
    /// by <see cref="GetCurrentJobs" />.  A client may
    /// thus observe "incorrect" results.  As a trade-off
    /// to simplify the current implementation, this
    /// race condition is accepted.
    /// </para>
    /// <para>
    /// This registration is separate from what <see cref="JobsManager" />
    /// does.  Registrations in the latter are keyed by 
    /// <see cref="PromiseId"/> to allow remote cancellation.
    /// The registration here is for monitoring jobs, which are
    /// not necessarily associated with a <see cref="PromiseId" />,
    /// but usually are.  Typically both registrations are required.
    /// Jobs are objects entirely internal to the job server and so 
    /// the registration here can be implemented in simpler ways,
    /// in particular, a (locked) linked list.
    /// </para>
    /// </remarks>
    /// <param name="job">
    /// The job to register as running.  
    /// </param>
    /// <returns>
    /// The return value is to be disposed once the
    /// job is finished.
    /// </returns>
    public RunningJobRegistration RegisterRunningJob(IRunningJob<PromisedWork> job)
    {
        // Avoid allocating node inside lock
        var node = new LinkedListNode<IRunningJob<PromisedWork>>(job);

        lock (_runningJobs)
            _runningJobs.AddLast(node);

        return new RunningJobRegistration(node);
    }

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
    /// Gets a snapshot of the items in the queue or currently executing,
    /// in summary form, for monitoring purposes.
    /// </summary>
    /// <param name="limit">
    /// If non-negative, return at most this number of items.
    /// </param>
    /// <returns>
    /// The list of jobs.  Those jobs that are currently running 
    /// or are closest to the head of the queue take precedence.
    /// This method does not return an enumerator or enumerable
    /// because it needs to take locks to observe the current jobs.
    /// </returns>
    public IReadOnlyList<IRunningJob<PromisedWork>> GetCurrentJobs(int limit = -1)
    {
        if (limit == 0)
            return Array.Empty<IRunningJob<PromisedWork>>();

        limit = (limit >= 0) ? limit : int.MaxValue;
        int capacity = Math.Min(Count, limit);

        int count = 0;
        var result = new List<IRunningJob<PromisedWork>>(capacity);

        lock (_runningJobs)
        {
            foreach (var job in _runningJobs)
            {
                result.Add(job);
                if (++count == limit)
                    return result;
            }
        }

        foreach (var item in _flow)
        {
            var macroJob = item.Multiple;
            if (macroJob is IRunningJob<PromisedWork> job)
                result.Add(job);
            else if (macroJob is null)
                result.Add(item.Single);
            else
                continue;

            if (++count == limit)
                return result;
        }

        return result;
    }

    /// <summary>
    /// Enumerator which yields a snapshot of the jobs in
    /// the queue or are currently running.
    /// </summary>
    /// <returns></returns>
    public IEnumerator<IRunningJob<PromisedWork>> GetEnumerator()
        => GetCurrentJobs().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Cancellation token associated with this queue.
    /// </summary>
    public CancellationToken CancellationToken => _cancellationSource.Token;

    /// <inheritdoc cref="ISchedulingAccount.CompletionStatistics" />
    public SchedulingStatistics CompletionStatistics => _flow.CompletionStatistics;

    /// <summary>
    /// Get the number of items currently in the queue or are running.
    /// </summary>
    /// <remarks>
    /// The returned value is a snapshot.  It may not be consistent
    /// with the actual number of items obtained from a subsequent
    /// call to <see cref="GetCurrentJobs" />
    /// or <see cref="GetEnumerator" />.
    /// </remarks>
    public int Count => _runningJobs.Count + _flow.Count;

    /// <summary>
    /// Credentials for the owner of this queue.
    /// </summary>
    /// <remarks>
    /// This information is only used for (administrative) display
    /// and does not affect the mechanics of queuing.
    /// </remarks>
    public ClaimsPrincipal? OwnerPrincipal { get; set; }
}
