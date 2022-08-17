using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Polly;

namespace Hearty.PollyProxies;

/// <summary>
/// Wraps <see cref="IAsyncEnumerable{T}" /> so that enumeration can be 
/// transparently restarted in case of faults. 
/// </summary>
/// <remarks>
/// <para>
/// Polly can only retry or limit calls to individual methods.  That is, Polly assumes
/// that re-calling a method is sufficient to retry the relevant operation.  This
/// assumption is not true for <see cref="IAsyncEnumerator{T}" /> that is producing
/// items from a stream of bytes (over the network).  If any call to 
/// <see cref="IAsyncEnumerator{T}.MoveNextAsync" /> faults, we must call
/// <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator" /> to restart the stream.
/// </para>
/// <para>
/// Furthermore, the items that have already been successfully produced up
/// to that point should not be duplicated.
/// </para>
/// </remarks>
public sealed class ResilientItemStream<T> : IAsyncEnumerable<KeyValuePair<int, T>>
{
    private readonly IAsyncEnumerable<KeyValuePair<int, T>> _source;
    private readonly IAsyncPolicy _policy;
    private readonly int _length;
    private readonly Context _context;

    /// <summary>
    /// Wraps an asynchronous sequence of items so retrieval goes through the Polly
    /// resilience framework.
    /// </summary>
    /// <param name="policy">
    /// Execution policy from Polly to run <see cref="IAsyncEnumerator{T}.MoveNextAsync" />
    /// against.
    /// </param>
    /// <param name="source">
    /// The original sequence of items.
    /// </param>
    /// <param name="length">
    /// The expected length of the sequence.
    /// </param>
    /// <param name="context">
    /// Context object to propagate to Polly.
    /// </param>
    public ResilientItemStream(IAsyncPolicy policy, 
                               IAsyncEnumerable<KeyValuePair<int, T>> source, 
                               int length,
                               Context context)
    {
        _source = source;
        _policy = policy;
        _length = length;
        _context = context;
    }

    /// <inheritdoc cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)" />
    public async IAsyncEnumerator<KeyValuePair<int, T>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var seenItems = new BitArray(_length);
        IAsyncEnumerator<KeyValuePair<int, T>>? s = null;

        // Do not re-create this delegate every time through the below loop
        Func<Context, Task<bool>> moveNextOrResetAsync = async context => 
        {
            s ??= _source.GetAsyncEnumerator(cancellationToken);

            try
            {
                return await s.MoveNextAsync().ConfigureAwait(false);
            }
            catch
            {
                await s.DisposeAsync().ConfigureAwait(false);
                s = null;
                throw;
            }
        };

        try
        {
            while (await _policy.ExecuteAsync(moveNextOrResetAsync, _context)
                                .ConfigureAwait(false))
            {
                var item = s!.Current;

                if (seenItems.Get(item.Key) == false)
                {
                    seenItems.Set(item.Key, true);
                    yield return item;
                }
            }
        }
        finally
        {
            if (s is not null)
                await s.DisposeAsync().ConfigureAwait(false);
        }
    }
}

