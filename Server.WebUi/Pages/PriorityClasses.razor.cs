using Hearty.Common;
using Microsoft.AspNetCore.Components;
using System;
using System.Collections.Generic;

namespace Hearty.Server.WebUi.Pages;

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

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        _isDisposed = true;
    }
}
