using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.WebUi.Infrastructure;

internal static class QueryableExtensions
{
    /// <summary>
    /// Get the list of rows from a LINQ query asynchronously.
    /// </summary>
    /// <typeparam name="T">Result type for one row from the query. </typeparam>
    /// <param name="source">The LINQ query, usually lazily evaluated from a database. </param>
    /// <param name="cancellationToken">
    /// Can be triggered to cancel the retrieval of results.
    /// </param>
    /// <returns>
    /// The list of rows from the query, loaded into memory.
    /// </returns>
    /// <remarks>
    /// The asynchronous loading is performed via <see cref="IAsyncEnumerable{T}" />
    /// if that is implemented by <paramref name="source" />.
    /// Otherwise, a background task is launched to load it (synchronously).
    /// This function is intended for UI components to render the results
    /// of the query, which should not block the UI thread, even if the
    /// database only supports synchronous operation, like SQLite.
    /// </remarks>
    public static ValueTask<IReadOnlyList<T>> LoadAsync<T>(this IQueryable<T> source, 
                                                           CancellationToken cancellationToken = default)
    {
        if (source is IAsyncEnumerable<T> asyncSource)
        {
            static async ValueTask<IReadOnlyList<T>> CopyFromAsyncEnumerableAsync(
                IAsyncEnumerable<T> asyncSource, CancellationToken cancellationToken)
            {
                var list = new List<T>();
                await foreach (var element in asyncSource.WithCancellation(cancellationToken)
                                                         .ConfigureAwait(false))
                    list.Add(element);
                return list;
            }

            return CopyFromAsyncEnumerableAsync(asyncSource, cancellationToken);
        }
        else
        {
            var task = Task.Factory.StartNew(arg =>
            {
                var (source, cancellationToken) = ((IEnumerable<T>, CancellationToken))arg!;
                source.TryGetNonEnumeratedCount(out int count);
                var list = new List<T>(capacity: count);
                foreach (var element in source)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    list.Add(element);
                }
                return (IReadOnlyList<T>)list;
            }, (source, cancellationToken), cancellationToken);

            return new(task);
        }
    }
}
