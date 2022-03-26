using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Hearty.Server;

/// <summary>
/// Basic implementation of <see cref="PromiseStorage" />
/// using in-process managed memory only.
/// </summary>
public class BasicPromiseStorage : PromiseStorage
{
    private readonly ConcurrentDictionary<PromiseId, Promise>
        _promisesById = new();

    private readonly ExpiryQueue<Promise> _expiryQueue;

    private class PromiseComparer : IComparer<Promise>
    {
#pragma warning disable CS8767 // Nullability of reference types in type of parameter doesn't match implicitly implemented member (possibly because of nullability attributes).
        public int Compare(Promise x, Promise y)
#pragma warning restore CS8767 // Nullability of reference types in type of parameter doesn't match implicitly implemented member (possibly because of nullability attributes).
            => x.Id.CompareTo(y.Id);
    }

    /// <summary>
    /// Prepare to storage <see cref="Promise" /> objects
    /// entirely in managed memory.
    /// </summary>
    public BasicPromiseStorage()
    {
        _expiryQueue = new ExpiryQueue<Promise>(new PromiseComparer(), this.ExpirePromise);
    }

    private DateTime GetDefaultPromiseExpiryTime(DateTime currentTime)
    {
        return currentTime + TimeSpan.FromMinutes(30);
    }

    private void ExpirePromise(Promise promise)
    {
        _promisesById.TryRemove(promise.Id, out _);
    }

    /// <inheritdoc />
    public override Promise CreatePromise(PromiseData? input,
                                          PromiseData? output = null)
    {
        var promise = CreatePromiseObject(input, output);
        var expiryTime = GetDefaultPromiseExpiryTime(promise.CreationTime);

        if (!_promisesById.TryAdd(promise.Id, promise))
            throw new InvalidOperationException("The promise with the newly generated ID already exists.  This should not happen. ");

        InvokeOnStorageEvent(new EventArgs { Type = OperationType.Create, PromiseId = promise.Id });

        _expiryQueue.ChangeExpiry(promise, expiryTime, SetPromiseExpiryDelegate);

        return promise;
    }

    private static readonly ExpiryExchangeFunc<Promise, DateTime?> 
        SetPromiseExpiryDelegate = SetPromiseExpiry;

    private static DateTime? SetPromiseExpiry(Promise target, in DateTime? suggestedExpiry, out DateTime? oldExpiry)
    {
        oldExpiry = target.Expiry;
        target.Expiry = suggestedExpiry;
        return suggestedExpiry;
    }

    /// <inheritdoc />
    public override Promise? GetPromiseById(PromiseId id)
    {
        _promisesById.TryGetValue(id, out var promise);
        return promise;
    }

    /// <inheritdoc />
    public override void SchedulePromiseExpiry(Promise promise, DateTime expiry)
    {
        InvokeOnStorageEvent(new EventArgs { Type = OperationType.ScheduleExpiry, PromiseId = promise.Id });

        if (!_promisesById.TryGetValue(promise.Id, out var value))
            throw new KeyNotFoundException($"Promise with key {promise.Id} was not created from this storage instance. ");

        _expiryQueue.ChangeExpiry(promise, expiry, SetPromiseExpiryDelegate);
    }
}
