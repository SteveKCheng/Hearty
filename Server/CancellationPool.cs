using System;
using System.Collections.Concurrent;
using System.Threading;

namespace JobBank.Server
{
    /// <summary>
    /// Provides instances of <see cref="CancellationTokenSource" />
    /// that are pooled to reduce initialization overhead.
    /// </summary>
    internal static class CancellationPool
    {
        private static readonly ConcurrentBag<CancellationTokenSource> _bag = new ConcurrentBag<CancellationTokenSource>();

        public struct Use : IDisposable
        {
            private CancellationTokenSource? _source;

            public CancellationTokenSource Source => _source ?? throw new ObjectDisposedException(nameof(CancellationPool.Use));

            public CancellationToken Token => Source.Token;

            public void Dispose()
            {
                var source = _source;

                if (source == null)
                    return;
                _source = null;

                if (source.TryReset())
                    _bag.Add(source);
                else
                    source.Dispose();
            }

            internal Use(CancellationTokenSource source) => _source = source;
        }

        public static Use Rent()
        {
            if (!_bag.TryTake(out var source))
                source = new CancellationTokenSource();

            return new Use(source);
        }

        public static Use CancelAfter(TimeSpan timeSpan, Action<object?> callback, object? state)
        {
            var use = Rent();
            var cts = use.Source;
            cts.CancelAfter(timeSpan);
            cts.Token.Register(callback, state);
            return use;
        }
    }
}
