using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Hearty.Utilities
{
    /// <summary>
    /// Provides instances of <see cref="CancellationTokenSource" />
    /// that are pooled to reduce initialization overhead.
    /// </summary>
    public static class CancellationSourcePool
    {
        private static readonly ConcurrentBag<CancellationTokenSource> _bag = new ConcurrentBag<CancellationTokenSource>();

        /// <summary>
        /// Holds an instance of <see cref="CancellationTokenSource" />
        /// rented from the pool.
        /// </summary>
        /// <remarks>
        /// On disposal, the instance is returned to the pool, if possible.
        /// If this structure is default-initialized, it refers to no instance in the pool.
        /// It is safe to neglect to dispose this structure; the cancellation source
        /// is simply not returned to the pool, and it will be garbage-collected
        /// when all references to it expire.
        /// </remarks>
        public struct Use : IDisposable
        {
            /// <summary>
            /// The rented cancellation source, if any.
            /// </summary>
            public CancellationTokenSource? Source { get; private set; }

            /// <summary>
            /// Cancellation token from the rented source, or the "none" token if
            /// there is no source.
            /// </summary>
            public CancellationToken Token => Source?.Token ?? CancellationToken.None;

            /// <summary>
            /// Returns the held cancellation source back into the pool, if possible.
            /// </summary>
            public void Dispose()
            {
                var source = Source;
                if (source == null)
                    return;

                Source = null;

                if (source.TryReset())
                {
                    _bag.Add(source);
                    return;
                }

                source.Dispose();
            }

            internal Use(CancellationTokenSource source) => Source = source;
        }

        /// <summary>
        /// Gets a rented cancellation source from the pool.
        /// </summary>
        public static Use Rent()
        {
            if (!_bag.TryTake(out var source))
                source = new CancellationTokenSource();

            return new Use(source);
        }

        /// <summary>
        /// Gets a rented cancellation source from the pool that is
        /// set to cancel itself after the specified time interval.
        /// </summary>
        /// <remarks>
        /// This method is intended for implementing time-outs.
        /// </remarks>
        public static Use CancelAfter(TimeSpan timeSpan)
        {
            var use = Rent();
            var cts = use.Source;
            cts!.CancelAfter(timeSpan);
            return use;
        }
    }
}
