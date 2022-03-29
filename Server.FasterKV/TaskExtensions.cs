using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.FasterKV;

internal static class TaskExtensions
{
    /// <summary>
    /// Synchronously wait for a task to complete.
    /// </summary>
    /// <typeparam name="T">The result from the asynchronous 
    /// operation. </typeparam>
    /// <param name="task">
    /// The task to wait upon.  
    /// </param>
    /// <param name="monitor">
    /// The object to use as the "monitor" (similar to a condition variable) 
    /// for synchronization between threads, through the method 
    /// <see cref="Monitor.Wait(object)" />.
    /// </param>
    /// <returns>
    /// The result from <paramref name="task" /> once it completes.
    /// If the task fails then its exception is propagated out.
    /// </returns>
    public static T Wait<T>(this in ValueTask<T> task, object monitor)
    {
        if (task.IsCompleted)
            return task.Result;

        var awaiter = task.ConfigureAwait(false).GetAwaiter();

        awaiter.UnsafeOnCompleted(() =>
        {
            // PulseAll is used instead of Pulse to tolerate
            // accidental sharing of the lock object.
            lock (monitor)
                Monitor.PulseAll(monitor);
        });

        lock (monitor)
        {
            // Block until the task is complete.  This loop should
            // only execute once if there are no spurious wake-ups.
            while (!task.IsCompleted)
                Monitor.Wait(monitor);
        }

        return awaiter.GetResult();
    }
}
