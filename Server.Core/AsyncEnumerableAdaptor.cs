using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Adapts a synchronous enumerable to an asynchronous one.
/// </summary>
internal class AsyncEnumerableAdaptor<T> : IAsyncEnumerable<T>
{
    private class Enumerator : IAsyncEnumerator<T>
    {
        public T Current => _original.Current;

        public ValueTask DisposeAsync()
        {
            _original.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_original.MoveNext());
        }

        public Enumerator(IEnumerator<T> original, CancellationToken cancellationToken)
        {
            _original = original;
            _cancellationToken = cancellationToken;
        }

        private readonly IEnumerator<T> _original;
        private readonly CancellationToken _cancellationToken;
    }

    public AsyncEnumerableAdaptor(IEnumerable<T> original) => _original = original;

    private readonly IEnumerable<T> _original;

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(_original.GetEnumerator(), cancellationToken);
}
