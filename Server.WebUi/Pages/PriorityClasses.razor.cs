using Hearty.Common;
using Hearty.Scheduling;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hearty.Server.WebUi.Pages;

/// <summary>
/// Blazor component to display the queues on the job server.
/// </summary>
public sealed partial class PriorityClasses : IDisposable
{
    private bool _isDisposed;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _timeoutProvider.Register(TimeoutBucket.After1Second,
            (_, _) =>
            {
                // TimeoutProvider does not provide cancellation, so
                // keep polling until the page is disposed.  False
                // positives can occur when this check races with
                // disposal, but they are harmless.
                if (_isDisposed)
                    return false;

                InvokeAsync(() =>
                {
                    // Definitely check this flag again after we
                    // enter the page's synchronization context to
                    // avoid races, assuming disposal also happens
                    // in the same synchronization context.
                    if (!_isDisposed)
                        StateHasChanged();
                });

                return true;
            }, null);
    }

    private HashSet<JobQueueKey>? _queuesToExpand;

    private bool ShouldDisplayInDetail(in JobQueueKey targetKey)
    {
        return _queuesToExpand?.Contains(targetKey) == true;
    }

    private void AddToDetailDisplay(in JobQueueKey targetKey)
    {
        var q = _queuesToExpand ??= new HashSet<JobQueueKey>();
        q.Add(targetKey);
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

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        _isDisposed = true;
    }
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
