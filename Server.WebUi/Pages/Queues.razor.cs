using Hearty.Common;
using Hearty.Scheduling;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hearty.Server.WebUi.Pages;

/// <summary>
/// Blazor component to display the queues on the job server.
/// </summary>
public sealed partial class Queues : TimedRefreshComponent
{
    /// <inheritdoc />
    protected override void OnInitialized() 
        => StartRefreshing(TimeoutBucket.After1Second);

    private Dictionary<JobQueueKey, bool>? _queuesToExpand;

    private void SetForDetailDisplay(in JobQueueKey targetKey, bool enable)
    {
        var q = _queuesToExpand ??= new Dictionary<JobQueueKey, bool>();

        // When a group key is toggled then all child keys should
        // inherit the new setting.  Implement by dropping all the
        // child keys.
        if (targetKey.Cohort is null)
        {
            bool noOwner = (targetKey.Owner is null);

            if (noOwner && targetKey.Priority is null)
            {
                q.Clear();
            }

            else
            {
                List<JobQueueKey>? keysToErase = null;
                foreach (var (key, _) in q)
                {
                    var groupKey = noOwner ? key.GetKeyForPriority()
                                           : key.GetKeyForOwnerAndPriority();
                    if (groupKey == targetKey && key != targetKey)
                    {
                        keysToErase ??= new();
                        keysToErase.Add(key);
                    }
                }

                if (keysToErase is not null)
                {
                    foreach (var key in keysToErase)
                        q.Remove(key);
                }
            }
        }

        q[targetKey] = enable;
    }

    private bool IsForDetailDisplay(in JobQueueKey targetKey)
    {
        var q = _queuesToExpand;
        if (q is null)
            return false;

        bool value;
        if (q.TryGetValue(targetKey, out value))
            return value;

        if (targetKey.Cohort is not null)
        {
            if (q.TryGetValue(targetKey.GetKeyForOwnerAndPriority(), out value))
                return value;

            if (targetKey.Owner is not null)
            {
                var t = targetKey.GetKeyForPriority();
                if (q.TryGetValue(targetKey.GetKeyForPriority(), out value))
                    return value;

                if (targetKey.Priority is not null)
                {
                    if (q.TryGetValue(new(), out value))
                        return value;
                }
            }
        }

        return false;
    }

    private static string GetStatusString(JobStatus status)
        => status switch
        {
            JobStatus.NotStarted => "Queued",
            JobStatus.Running => "Running",
            JobStatus.Succeeded => "Succeeded",
            JobStatus.Faulted => "Failed",
            JobStatus.Cancelled => "Cancelled",
            _ => string.Empty
        };
}

/// <summary>
/// Represents a job item in the display dashboard.
/// </summary>
internal struct DisplayedJob
{
    /// <summary>
    /// The key for the queue where this job comes from.
    /// </summary>
    public JobQueueKey Queue { get; }

    /// <summary>
    /// The sequence number of this job from its queue,
    /// 0 referring to the front of the queue.
    /// </summary>
    public int Ordinal { get; }

    /// <summary>
    /// Details about the job.
    /// </summary>
    public IRunningJob<PromisedWork> Job { get; }

    /// <summary>
    /// Constructor.
    /// </summary>
    public DisplayedJob(JobQueueKey queue, int ordinal, IRunningJob<PromisedWork> job)
    {
        Queue = queue;
        Ordinal = ordinal;
        Job = job;
    }
}
