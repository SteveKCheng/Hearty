using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// A unique identifier for a job queue 
/// <see cref="IJobQueueSystem" />.
/// </summary>
/// <param name="Owner">Representation of the owner of the queue. </param>
/// <param name="Priority">
/// The priority class, numbered from 0 to 
/// <see cref="IJobQueueSystem.PriorityClassesCount" /> minus one.
/// </param>
/// <param name="Name">
/// The name of the queue within the set of queues with the
/// same owner and priority class.
/// </param>
public record struct JobQueueKey(IJobQueueOwner Owner, 
                                 int Priority,
                                 string Name);

/// <summary>
/// Provides access to the job queues for all clients.
/// </summary>
public interface IJobQueueSystem
{
    /// <summary>
    /// Get the queue to push jobs into for a given client,
    /// creating it if it does not exist.
    /// </summary>
    /// <param name="key">Identifier assigned to the job queue. 
    /// </param>
    /// <returns>
    /// The desired queue, which may be newly created.
    /// </returns>
    ClientJobQueue GetOrAddJobQueue(JobQueueKey key);

    /// <summary>
    /// Get the queue to push jobs into for a given client,
    /// if it exists.
    /// </summary>
    /// <param name="key">Identifier assigned to the job queue. 
    /// </param>
    /// <returns>
    /// The desired queue, or null if it has not been created
    /// before or has expired.
    /// </returns>
    ClientJobQueue? TryGetJobQueue(JobQueueKey key);

    /// <summary>
    /// The number of priority classes available.
    /// </summary>
    int PriorityClassesCount { get; }

    /// <summary>
    /// Get the weight assigned to a priority class.
    /// </summary>
    /// <param name="priority">
    /// The priority class, numbered from 0 to 
    /// <see cref="PriorityClassesCount" /> minus one.
    /// </param>
    /// <returns>
    /// The assigned weight as a positive integer.
    /// </returns>
    int GetPriorityClassWeight(int priority);

    /// <summary>
    /// List out the queues for one priority class and queue owner.
    /// </summary>
    /// <param name="owner">Representation of the owner of the queue. </param>
    /// <param name="priority">
    /// The priority class, numbered from 0 to 
    /// <see cref="IJobQueueSystem.PriorityClassesCount" /> minus one.
    /// </param>
    /// <returns>
    /// Snapshot of the list of queues.
    /// </returns>
    IReadOnlyList<KeyValuePair<string, ClientJobQueue>> 
        GetClientQueues(IJobQueueOwner owner, int priority);

    /// <summary>
    /// List out the queues for one priority class.
    /// </summary>
    /// <param name="priority">
    /// The priority class, numbered from 0 to 
    /// <see cref="IJobQueueSystem.PriorityClassesCount" /> minus one.
    /// </param>
    /// <returns>
    /// Snapshot of the list of queues.
    /// </returns>
    IReadOnlyList<KeyValuePair<JobQueueKey, ClientJobQueue>>
        GetClientQueues(int priority);
}
