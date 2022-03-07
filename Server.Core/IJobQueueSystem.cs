using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Hearty.Common;

namespace Hearty.Server;

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
    /// <param name="ownerPrincipal">
    /// "Principal" object describing the owner of the queue
    /// if the queue is to be created.  Ignored if the queue
    /// already exists.
    /// </param>
    /// <returns>
    /// The desired queue, which may be newly created.
    /// </returns>
    ClientJobQueue GetOrAddJobQueue(JobQueueKey key,
                                    ClaimsPrincipal? ownerPrincipal);

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
    /// <param name="owner">Identifies the owner of the queue. </param>
    /// <param name="priority">
    /// The priority class, numbered from 0 to 
    /// <see cref="IJobQueueSystem.PriorityClassesCount" /> minus one.
    /// </param>
    /// <returns>
    /// Snapshot of the list of queues.
    /// </returns>
    IReadOnlyList<KeyValuePair<string, ClientJobQueue>> 
        GetClientQueues(string owner, int priority);

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
