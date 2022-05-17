using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Adapts a synchronous enumerable to an asynchronous one.
/// </summary>
internal class AsyncEnumerableAdaptor<T> : IAsyncEnumerable<T>, ICountable
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

    public int Count
    {
        get
        {
            if (_original is IReadOnlyCollection<T> c)
                return c.Count;
            else
                return -1;
        }
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(_original.GetEnumerator(), cancellationToken);
}

/// <summary>
/// Provides a count of items for a collection.
/// </summary>
/// <remarks>
/// The collection might not necessarily be synchronously enumerable;
/// otherwise the existing standard interface <see cref="IReadOnlyCollection{T}" />
/// could be used.
/// </remarks>
internal interface ICountable
{
    /// <summary>
    /// Hint of the number of items in the collection.  
    /// </summary>
    /// <remarks>
    /// This number is negative if the number of items cannot be determined,
    /// in a quick synchronous manner.
    /// </remarks>
    int Count { get; }
}
