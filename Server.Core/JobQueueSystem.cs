using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JobBank.Scheduling;

namespace JobBank.Server;

using JobMessage = ILaunchableJob<PromisedWork, PromiseData>;

/// <summary>
/// Manages a system of queues for scheduling clients' jobs.
/// </summary>
public sealed class JobQueueSystem : IJobQueueSystem, IAsyncDisposable, IDisposable
{
    private readonly PrioritizedQueueSystem<
                        JobMessage,
                        ClientQueueSystem<
                            JobMessage,
                            IJobQueueOwner,
                            ClientQueueSystem<
                                JobMessage,
                                string,
                                ClientJobQueue>>> _priorityClasses;

    private readonly SimpleExpiryQueue _expiryQueue;

    /// <inheritdoc cref="IJobQueueSystem.PriorityClassesCount" />
    public int PriorityClassesCount => _priorityClasses.Count;

    private ClientQueueSystem<JobMessage,
                              string,
                              ClientJobQueue> 
        CreateInnerQueueSystem()
    {
        return new(factory: key => new ClientJobQueue(),
                   _expiryQueue);
    }

    private IEnumerable<ClientQueueSystem<
                            JobMessage, 
                            IJobQueueOwner,
                            ClientQueueSystem<
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
    public ClientJobQueue GetOrAddJobQueue(JobQueueKey key)
    {
        return _priorityClasses[key.Priority].GetOrAdd(key.Owner).GetOrAdd(key.Name);
    }

    /// <inheritdoc cref="IJobQueueSystem.TryGetJobQueue" />
    public ClientJobQueue? TryGetJobQueue(JobQueueKey key)
    {
        var priorityClass = _priorityClasses[key.Priority];
        if (!priorityClass.TryGetValue(key.Owner, out var innerQueueSystem))
            return null;

        if (!innerQueueSystem.TryGetValue(key.Name, out var clientJobQueue))
            return null;

        return clientJobQueue;
    }

    /// <summary>
    /// Asynchronously stop dispatching clients' jobs to workers.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _cancelSource.Cancel();
        return new ValueTask(_jobRunnerTask);
    }

    /// <summary>
    /// Stop dispatching clients' jobs to workers, 
    /// synchronously waiting until the internal dispatching task completes.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().Wait();

    /// <summary>
    /// Construct with a fixed number of priority classes.
    /// </summary>
    /// <param name="countPriorities">
    /// The desired number of priority classes.
    /// </param>
    public JobQueueSystem(int countPriorities,
                          WorkerDistribution<PromisedWork, PromiseData> workerDistribution)
    {
        _expiryQueue = new SimpleExpiryQueue(60000, 20);
        _priorityClasses = new(GenerateMiddleQueueSystems(countPriorities));

        _cancelSource = new CancellationTokenSource();

        _jobRunnerTask = JobScheduling.RunJobsAsync(
                            _priorityClasses.AsChannel(),
                            workerDistribution.AsChannel(),
                            _cancelSource.Token);
    }

    /// <summary>
    /// Used to stop <see cref="_jobRunnerTask" />.
    /// </summary>
    private readonly CancellationTokenSource _cancelSource;

    /// <summary>
    /// Task that de-queues jobs and dispatches them to workers.
    /// </summary>
    private readonly Task _jobRunnerTask;
}
