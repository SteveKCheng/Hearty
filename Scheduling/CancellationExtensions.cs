﻿using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Scheduling;

/// <summary>
/// Extension methods dealing with cancellation.
/// </summary>
public static class CancellationExtensions
{
    /// <summary>
    /// Triggers cancellation as a background task, so that
    /// callbacks are not synchronously executed.
    /// </summary>
    /// <param name="source">
    /// The cancellation source to trigger.
    /// </param>
    /// <remarks>
    /// This method helps to lower the latency of processing
    /// cancellations in server applications, when the amount of
    /// code that have been registered as callbacks to the cancellation
    /// token cannot be (statically) bounded.
    /// </remarks>
    public static void CancelInBackground(this CancellationTokenSource source)
    {
        Task.Factory.StartNew(o => Unsafe.As<CancellationTokenSource>(o!).Cancel(),
                              source);
    }

    /// <summary>
    /// Triggers cancellation, as a background task depending
    /// on a run-time flag.
    /// </summary>
    /// <param name="source">
    /// The cancellation source to trigger.
    /// </param>
    /// <param name="background">
    /// Whether the cancellation should be in the background.
    /// </param>
    public static void CancelMaybeInBackground(this CancellationTokenSource source,
                                               bool background)
    {
        if (background)
            source.CancelInBackground();
        else
            source.Cancel();
    }

    /// <summary>
    /// Run <see cref="IAsyncDisposable.DisposeAsync" /> in the background
    /// and ignore any exceptions.
    /// </summary>
    public static async Task FireAndForgetDisposeAsync(this IAsyncDisposable target)
    {
        try
        {
            await target.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Run <see cref="IAsyncDisposable.DisposeAsync" /> in the background,
    /// and ignore the last item plus any exceptions.
    /// </summary>
    public static async Task FireAndForgetDisposeAsync<T>(this IAsyncDisposable target, ValueTask<T> lastItem)
    {
        try
        {
            await lastItem.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await target.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Make one cancellation token cancel another cancellation source.
    /// </summary>
    /// <param name="source">
    /// The cancellation source to propagate cancellation to.
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token to observe and propagate cancellation from.
    /// </param>
    /// <returns>
    /// Registration on <paramref name="cancellationToken" />
    /// that should be disposed when cancellation should no longer
    /// be propagated.
    /// </returns>
    public static CancellationTokenRegistration 
        LinkOtherToken(this CancellationTokenSource source, 
                       CancellationToken cancellationToken)
    {
        return cancellationToken.UnsafeRegister(
                o => Unsafe.As<CancellationTokenSource>(o!).Cancel(),
                source);
    }
}
