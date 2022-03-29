using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.FasterKV;

internal static class TaskExtensions
{
    // Operations in FASTER KV can become "pending" if they need
    // filesystem/device I/O.  The calling thread needs to
    // be blocked for <see cref="FasterDbDictionary{TKey, TValue}" />
    // to present a synchronous interface.
    //
    // FASTER KV can report completion using direct callbacks,
    // but it does not propagate the status of delete and
    // upsert operations.  It appears to be an oversight
    // in its API.  Thus we must use the asynchronous version
    // of its APIs and wire synchronous waiting ourselves
    // through the following extension method.

    /// <summary>
    /// Synchronously wait for a task to complete.
    /// </summary>
    /// <typeparam name="T">The result from the asynchronous 
    /// operation. </typeparam>
    /// <param name="task">
    /// The task to wait upon.  
    /// </param>
    /// <returns>
    /// The result from <paramref name="task" /> once it completes.
    /// If the task fails then its exception is propagated out.
    /// </returns>
    public static T Wait<T>(this in ValueTask<T> task)
    {
        if (task.IsCompleted)
            return task.Result;

        var awaiter = task.ConfigureAwait(false).GetAwaiter();

        var waker = new Waker();
        awaiter.UnsafeOnCompleted(waker.Wake);

        lock (waker)
        {
            // Block until the task is complete.  This loop should
            // only execute once if there are no spurious wake-ups.
            while (!task.IsCompleted)
                Monitor.Wait(waker);
        }

        return awaiter.GetResult();
    }

    private sealed class Waker
    {
        public void Wake()
        {
            lock (this)
                Monitor.PulseAll(this);
        }
    }
}
