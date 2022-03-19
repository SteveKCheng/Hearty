using System;
using Microsoft.AspNetCore.Components;

namespace Hearty.Server.WebUi;

/// <summary>
/// A Blazor component that refreshes itself at periodic
/// time intervals.
/// </summary>
public class TimedRefreshComponent : ComponentBase, IDisposable
{
    private bool _isDisposed;
    private bool _isRefreshing;

    /// <summary>
    /// Provides timers that can scale to service many clients/components.
    /// </summary>
    [Inject]
    private TimeoutProvider TimeoutProvider { get; set; } = null!;

    /// <summary>
    /// Start the timer that periodically refreshes this component.
    /// </summary>
    protected void StartRefreshing(TimeoutBucket timeoutBucket)
    {
        if (_isRefreshing)
            return;

        TimeoutProvider.Register(timeoutBucket,
            (_, _) =>
            {
                // TimeoutProvider does not provide cancellation, so
                // keep polling until the page is disposed.  False
                // positives can occur when this check races with
                // disposal, but they are harmless.
                if (!_isRefreshing)
                    return false;

                InvokeAsync(() =>
                {
                    // Definitely check this flag again after we
                    // enter the page's synchronization context to
                    // avoid races, assuming disposal also happens
                    // in the same synchronization context.
                    if (_isRefreshing)
                        StateHasChanged();
                });

                return true;
            }, null);

        _isRefreshing = true;
    }

    /// <summary>
    /// Stop the timer that periodically refreshes this component.
    /// </summary>
    protected void StopRefreshing()
    {
        _isRefreshing = false;
    }

    /// <summary>
    /// Allows derived classes to dispose their own resources.
    /// </summary>
    /// <remarks>
    /// This method implements the "dispose pattern".
    /// Derived classes should always call the base class
    /// implementation.
    /// </remarks>
    /// <param name="disposing">
    /// True if this instance is being disposed; false if
    /// it is being finalized.
    /// </param>
    protected virtual void DisposeImpl(bool disposing)
    {
        _isRefreshing = false;
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        if (!_isDisposed)
        {
            DisposeImpl(disposing: true);
            _isDisposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
