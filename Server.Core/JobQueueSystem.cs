using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Scheduling;
using Hearty.Common;
using System.Security.Claims;

namespace Hearty.Server;

using JobMessage = ILaunchableJob<PromisedWork, PromiseData>;

/// <summary>
/// Manages a system of queues for scheduling clients' jobs.
/// </summary>
public sealed class JobQueueSystem : IJobQueueSystem, IAsyncDisposable, IDisposable
{
    private readonly PrioritizedQueueSystem<
                        JobMessage,
                        KeyedQueueSystem<
                            JobMessage,
                            string,
                            KeyedQueueSystem<
                                JobMessage,
                                string,
                                ClientJobQueue>>> _priorityClasses;

    private readonly WorkerDistribution<PromisedWork, PromiseData> _workerDistribution;

    private readonly SimpleExpiryQueue _expiryQueue;

    /// <inheritdoc cref="IJobQueueSystem.PriorityClassesCount" />
    public int PriorityClassesCount => _priorityClasses.Count;

    public int DefaultPriority => 5;

    private KeyedQueueSystem<JobMessage,
                              string,
                              ClientJobQueue> 
        CreateInnerQueueSystem()
    {
        return new(factory: key => new ClientJobQueue(),
                   _expiryQueue);
    }

    private IEnumerable<KeyedQueueSystem<
                            JobMessage, 
                            string,
                            KeyedQueueSystem<
                                JobMessage,
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

    /// <inheritdoc cref="IJobQueueSystem.GetClientQueues(IJobQueueOwner, int)" />
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
    public JobQueueSystem(int countPriorities,
                          WorkerDistribution<PromisedWork, PromiseData> workerDistribution)
    {
        _expiryQueue = new SimpleExpiryQueue(60000, 20);
        _priorityClasses = new(GenerateMiddleQueueSystems(countPriorities));
        _workerDistribution = workerDistribution;
        _cancelSource = new CancellationTokenSource();

        for (int i = 0; i < countPriorities; ++i)
            _priorityClasses.ResetWeight(priority: i, weight: (i + 1) * 10);

        // Do not eagerly run RunJobsAsync inside this constructor.
        // This is the workaround for "Task.Yield" + "ConfigureAwait(false)":
        //   https://stackoverflow.com/questions/28309185/task-yield-in-library-needs-configurewaitfalse
        _jobRunnerTask = Task.Run(() => JobScheduling.RunJobsAsync(
                                            _priorityClasses.AsChannel(),
                                            _workerDistribution.AsChannel(),
                                            throwOnCancellation: false,
                                            _cancelSource.Token), 
                                  _cancelSource.Token);
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
