using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Scheduling;
using Hearty.Common;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Hearty.Server;

/// <summary>
/// Manages a system of queues for scheduling clients' jobs.
/// </summary>
public sealed class JobQueueSystem : IJobQueueSystem, IAsyncDisposable, IDisposable
{
    private readonly PrioritizedQueueSystem<
                        ClientJobMessage,
                        KeyedQueueSystem<
                            ClientJobMessage,
                            string,
                            KeyedQueueSystem<
                                ClientJobMessage,
                                string,
                                ClientJobQueue>>> _priorityClasses;

    private readonly WorkerDistribution<PromisedWork, PromiseData> _workerDistribution;

    private readonly SimpleExpiryQueue _expiryQueue;

    private readonly ILogger _logger;

    private readonly JobServerMetrics _metrics;

    /// <inheritdoc cref="IJobQueueSystem.PriorityClassesCount" />
    public int PriorityClassesCount => _priorityClasses.Count;

    /// <summary>
    /// The default assumed when looking up a queue
    /// without a specified priority class.
    /// </summary>
    public int DefaultPriority => 5;

    private KeyedQueueSystem<ClientJobMessage,
                              string,
                              ClientJobQueue> 
        CreateInnerQueueSystem()
    {
        return new(factory: key => new ClientJobQueue(),
                   _expiryQueue);
    }

    private IEnumerable<KeyedQueueSystem<
                            ClientJobMessage, 
                            string,
                            KeyedQueueSystem<
                                ClientJobMessage,
                                string,
                                ClientJobQueue>>>
        GenerateMiddleQueueSystems(int count)
    {
        for (int i = 0; i < count; ++i)
        {
            yield return new(factory: key => CreateInnerQueueSystem(),
                             _expiryQueue);
        }
    }

    /// <inheritdoc cref="IJobQueueSystem.GetOrAddJobQueue" />
    public ClientJobQueue GetOrAddJobQueue(JobQueueKey key,
                                           ClaimsPrincipal? ownerPrincipal)
    {
        var priority = key.Priority ?? DefaultPriority;
        var cohort = key.Cohort ?? string.Empty;
        var owner = key.Owner ?? string.Empty;

        var queue = _priorityClasses[priority].GetOrAdd(owner)
                                              .GetOrAdd(cohort, 
                                                        out bool exists);

        if (!exists)
            queue.OwnerPrincipal = ownerPrincipal;

        return queue;
    }

    /// <inheritdoc cref="IJobQueueSystem.TryGetJobQueue" />
    public ClientJobQueue? TryGetJobQueue(JobQueueKey key)
    {
        var priority = key.Priority ?? DefaultPriority;
        var cohort = key.Cohort ?? string.Empty;
        var owner = key.Owner ?? string.Empty;

        var priorityClass = _priorityClasses[priority];
        if (!priorityClass.TryGetValue(owner, out var innerQueueSystem))
            return null;

        if (!innerQueueSystem.TryGetValue(cohort, out var clientJobQueue))
            return null;

        return clientJobQueue;
    }

    /// <summary>
    /// Asynchronously stop dispatching clients' jobs to workers.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _priorityClasses.TerminateChannel();
        _workerDistribution.TerminateChannel();
        _cancelSource.Cancel();
        
        return new ValueTask(_jobRunnerTask);
    }

    /// <summary>
    /// Stop dispatching clients' jobs to workers, 
    /// synchronously waiting until the internal dispatching task completes.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().Wait();

    /// <inheritdoc cref="IJobQueueSystem.GetPriorityClassWeight" />
    public int GetPriorityClassWeight(int priority) 
        => _priorityClasses[priority].AsFlow().Weight;

    /// <inheritdoc cref="IJobQueueSystem.GetClientQueues(string, int)" />
    public IReadOnlyList<KeyValuePair<string, ClientJobQueue>> 
        GetClientQueues(string owner, int priority)
    {
        var priorityClass = _priorityClasses[priority];
        if (!priorityClass.TryGetValue(owner, out var innerQueueSystem))
            return Array.Empty<KeyValuePair<string, ClientJobQueue>>();

        return innerQueueSystem.ListMembers();
    }

    /// <inheritdoc cref="IJobQueueSystem.GetClientQueues(int)" />
    public IReadOnlyList<KeyValuePair<JobQueueKey, ClientJobQueue>>
        GetClientQueues(int priority)
    {
        var priorityClass = _priorityClasses[priority];
        if (priorityClass.Count == 0)
            return Array.Empty<KeyValuePair<JobQueueKey, ClientJobQueue>>();

        var result = new List<KeyValuePair<JobQueueKey, ClientJobQueue>>();

        foreach (var (owner, innerQueueSystem) in priorityClass.ListMembers())
        {
            var innerMembers = innerQueueSystem.ListMembers();
            result.EnsureCapacity(result.Count + innerMembers.Length);

            foreach (var (name, queue) in innerMembers)
            {
                var key = new JobQueueKey(owner, priority, name);
                result.Add(new(key, queue));
            }
        }

        return result;
    }

    /// <summary>
    /// Construct with a fixed number of priority classes.
    /// </summary>
    /// <param name="countPriorities">
    /// The desired number of priority classes for jobs.  
    /// This number is typically constant for the application.  
    /// The actual weights for each priority class are dynamically 
    /// adjustable.
    /// </param>
    /// <param name="workerDistribution">
    /// The set of workers that can accept jobs to execute
    /// as they come off the job queues.
    /// </param>
    /// <param name="logger">
    /// Logs status and error messages.
    /// </param>
    /// <param name="metrics">
    /// Metrics on enqueued jobs will be recorded into here.
    /// </param>
    public JobQueueSystem(int countPriorities,
                          WorkerDistribution<PromisedWork, PromiseData> workerDistribution,
                          ILogger<JobQueueSystem> logger,
                          JobServerMetrics? metrics = null)
    {
        _logger = logger;
        _expiryQueue = new SimpleExpiryQueue(60000, 20);
        _priorityClasses = new(GenerateMiddleQueueSystems(countPriorities));
        _workerDistribution = workerDistribution;
        _cancelSource = new CancellationTokenSource();

        for (int i = 0; i < countPriorities; ++i)
            _priorityClasses.ResetWeight(priority: i, weight: (i + 1) * 10);

        // Do not eagerly run RunJobsAsync inside this constructor.
        // This is the workaround for "Task.Yield" + "ConfigureAwait(false)":
        //   https://stackoverflow.com/questions/28309185/task-yield-in-library-needs-configurewaitfalse
        _jobRunnerTask = Task.Run(() => RunJobsAsync(
                                            _priorityClasses.AsChannel(),
                                            _workerDistribution.AsChannel(),
                                            _cancelSource.Token), 
                                  _cancelSource.Token);

        _metrics = metrics ?? JobServerMetrics.Default;
    }

    /// <summary>
    /// Assign jobs from a channel as soon as resources to execute that
    /// job are made available from another channel.
    /// </summary>
    /// <param name="jobMessagesChannel">
    /// Presents the jobs to execute in a potentially non-ending sequence.
    /// The channel may be implemented by a queuing system like the one
    /// from this library.
    /// </param>
    /// <param name="vacanciesChannel">
    /// Presents claims to resources to execute the jobs in a potentially
    /// non-ending sequence.  This channel may also be implemented by a queuing system 
    /// like the one from this library.
    /// </param>
    /// <param name="cancellationToken">
    /// Can be used to interrupt processing of jobs.
    /// </param>
    /// <returns>
    /// Asynchronous task that completes when interrupted by 
    /// <paramref name="cancellationToken" />, or when at least one
    /// of the channels is closed.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> signals cancellation.
    /// </exception>
    private async Task RunJobsAsync(
        ChannelReader<ClientJobMessage> jobMessagesChannel,
        ChannelReader<JobVacancy<PromisedWork, PromiseData>> vacanciesChannel,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await vacanciesChannel.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await jobMessagesChannel.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    break;

                if (vacanciesChannel.TryRead(out var vacancy))
                {
                    bool consumed = false;

                    try
                    {
                        if (jobMessagesChannel.TryRead(out var clientJob))
                        {
                            consumed = true;
                            if (vacancy.TryLaunchJob(clientJob.Job))
                                _ = RegisterJobWhileRunningAsync(clientJob);
                        }
                    }
                    finally
                    {
                        if (!consumed)
                            vacancy.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException e) when (e.CancellationToken == cancellationToken)
        {
        }
    }

    /// <summary>
    /// Register a job that has just been de-queued as running,
    /// then unregister it when the job finishes.
    /// </summary>
    private async Task RegisterJobWhileRunningAsync(ClientJobMessage clientJob)
    {
        using var _ = clientJob.Queue.RegisterRunningJob(clientJob.Job);
        _metrics.CountJobsStarted.Add(1);
        try
        {
            await clientJob.Job.OutputTask.ConfigureAwait(false);
        }
        catch 
        { 
        }
        _metrics.CountJobsFinished.Add(1);
    }

    /// <summary>
    /// Used to stop <see cref="_jobRunnerTask" /> even when there are
    /// still jobs in the channel.
    /// </summary>
    private readonly CancellationTokenSource _cancelSource;

    /// <summary>
    /// Task that de-queues jobs and dispatches them to workers.
    /// </summary>
    private readonly Task _jobRunnerTask;
}
