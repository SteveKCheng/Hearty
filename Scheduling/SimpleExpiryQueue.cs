using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Hearty.Scheduling
{
    /// <summary>
    /// Clean-up action that can be invoked by <see cref="SimpleExpiryQueue" />.
    /// </summary>
    /// <param name="now">The current time, measured in the same way 
    /// as <see cref="Environment.TickCount64" />, when the current
    /// clean-up action is being processed.
    /// </param>
    /// <param name="state">
    /// The arbitrary object registered with <see cref="SimpleExpiryQueue.Enqueue" />.
    /// </param>
    /// <returns>
    /// True if the clean-up action should be re-queued, false if not.
    /// </returns>
    public delegate bool ExpiryAction(long now, object? state);

    /// <summary>
    /// Cleans up registered objects after a certain time interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class provides time-based garbage collection, essentially.
    /// Its functionality is deliberately limited to enable a simple 
    /// and fast implementation.
    /// </para>
    /// <para>
    /// It is designed to scale for thousands of objects being infrequently
    /// registered.  It uses minimal resources, e.g. clean-ups for all the
    /// objects are triggered by one shared <see cref="Timer" /> object.
    /// </para>
    /// <para>
    /// Note that the current implementation of .NET, whenever it fires
    /// any instance of <see cref="Timer" />, it must linearly scan 
    /// a (global) list of all active instances!  This very sub-optimal 
    /// implementation thus requires user-level code to coalesce its own 
    /// timers to achieve any reasonable degree of scalability.
    /// </para>
    /// <para>
    /// To avoid the need to sort the registered objects,
    /// it is not possible to unregister objects, and the time to expiry
    /// is the same for all objects.
    /// </para>
    /// <para>
    /// The registered expiry actions are executed in sequence.
    /// In contrast, if individual <see cref="Timer" /> timers are used
    /// then the actions may fire in parallel, via .NET's standard thread 
    /// pool.  Again, that may be bad for scalability when there could be
    /// hundreds of expiry actions.
    /// </para>
    /// <para>
    /// The expiry actions registered with this class should be quick to 
    /// execute, so that it makes sense execute them in sequence.  
    /// As the relative expiry time is constant, and registered actions cannot
    /// be cancelled, if the objects have varying lifetimes, then 
    /// effectively they need to be polled.  So if an expiry action 
    /// executes before the target object has really expired, 
    /// it should do nothing but get itself re-queued, either
    /// immediately or later upon receiving some other callback.
    /// The latter approach should be quite parsimonious of resources.
    /// </para>
    /// </remarks>
    public class SimpleExpiryQueue : IDisposable
    {
        /// <summary>
        /// One entry in the expiration queue.
        /// </summary>
        private readonly struct Entry
        {
            /// <summary>
            /// Deadline expressed using the same monotonic clock
            /// as <see cref="Environment.TickCount64" />.
            /// </summary>
            public readonly long Deadline;

            /// <summary>
            /// The clean-up action to invoke.
            /// </summary>
            public readonly ExpiryAction Action;

            /// <summary>
            /// State object passed to <see cref="Action" />.
            /// </summary>
            public readonly object? State;

            public Entry(long deadline, ExpiryAction action, object? state)
            {
                Deadline = deadline;
                Action = action;
                State = state;
            }
        }

        /// <summary>
        /// Expirations to process in chronological order.
        /// </summary>
        private readonly Queue<Entry> _queue;

        /// <summary>
        /// Shared timer to trigger consuming <see cref="_queue" />.
        /// </summary>
        private readonly Timer _timer;

        /// <summary>
        /// Approximate time interval to clean up the next object,
        /// in milliseconds.
        /// </summary>
        public int ExpiryTicks { get; }

        /// <summary>
        /// The implementation-defined minimum value for <see cref="ExpiryTicks" />: 20 milliseconds.
        /// </summary>
        /// <remarks>
        /// Note that .NET timers, depending on the underlying platform, may well
        /// have a resolution of only 10-20 milliseconds.
        /// </remarks>
        public static int MinimumTicks { get; } = 20;

        /// <summary>
        /// The implementation-defined maximum value for <see cref="ExpiryTicks" />: 24 hours.
        /// </summary>
        public static int MaximumTicks { get; } = 1000 * 60 * 60 * 24;

        /// <summary>
        /// Prepare the queue where objects may be registered for clean-up..
        /// </summary>
        /// <param name="expiryTicks">
        /// The desired value for <see cref="ExpiryTicks"/>. </param>
        /// <param name="capacity">
        /// The number of objects queued for cleaned up simultaneous
        /// to initially allocate capacity for.
        /// </param>
        public SimpleExpiryQueue(int expiryTicks, int capacity)
        {
            if (expiryTicks <= 0 || expiryTicks > MaximumTicks)
                throw new ArgumentOutOfRangeException(nameof(expiryTicks));

            _queue = new Queue<Entry>(capacity);

            _timer = new Timer(s => Unsafe.As<SimpleExpiryQueue>(s!).OnTimerFiring(),
                               this, Timeout.Infinite, Timeout.Infinite);

            ExpiryTicks = Math.Max(expiryTicks, MinimumTicks);
        }

        /// <summary>
        /// Called by <see cref="_timer" /> when it fires.
        /// Consumes entries in the queue up to the first one
        /// that is not expired yet.
        /// </summary>
        private void OnTimerFiring()
        {
            Entry entry;
            bool more;

            lock (_queue)
            {
                more = _queue.TryPeek(out entry);
            }

            while (more)
            {
                // Stop de-queuing and re-schedule upon reaching
                // an entry that should not be expiring yet
                long now = Environment.TickCount64;
                if (entry.Deadline > now)
                {
                    _timer.Change(Math.Max(ExpiryTicks, entry.Deadline - now),
                                  Timeout.Infinite);
                    break;
                }

                bool toRequeue = false;
                try
                {
                    toRequeue = entry.Action.Invoke(now, entry.State);
                }
                catch
                {
                    // FIXME report as event
                }

                long deadline = toRequeue ? Environment.TickCount64 + ExpiryTicks 
                                          : default;

                lock (_queue)
                {
                    _queue.Dequeue();

                    if (toRequeue)
                        _queue.Enqueue(new Entry(deadline, entry.Action, entry.State));

                    more = _queue.TryPeek(out entry);
                }
            }
        }

        /// <summary>
        /// Register an object to be processed after its
        /// "time to expiry".
        /// </summary>
        /// <param name="action">
        /// The function to invoke after a time interval of about
        /// <see cref="ExpiryTicks" /> expires.
        /// </param>
        /// <param name="state">
        /// Arbitrary object to pass into <paramref name="action" />.
        /// </param>
        /// <returns>
        /// The approximate deadline when the action will be invoked.
        /// This deadline time is measured in the same way as
        /// <see cref="Environment.TickCount64" />.
        /// </returns>
        public long Enqueue(ExpiryAction action, object? state)
        {
            var now = Environment.TickCount64;
            var deadline = now + ExpiryTicks;
            bool toScheduleTimer;
            lock (_queue)
            {
                toScheduleTimer = (_queue.Count == 0);
                _queue.Enqueue(new Entry(deadline, action, state));
            }

            if (toScheduleTimer)
                _timer.Change(ExpiryTicks, Timeout.Infinite);

            return deadline;
        }

        /// <inheritdoc cref="IDisposable.Dispose" />
        public void Dispose()
        {
            _timer.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
