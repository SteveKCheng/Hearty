using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Hearty.Server.FasterKV;

public partial class FasterDbPromiseStorage
{
    /// <summary>
    /// Promises that may have a current live representation as .NET objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value of each entry in this dictionary points to an instance
    /// of <see cref="Promise" /> or a <see cref="WeakReference{T}" /> to one.
    /// </para>
    /// <para>
    /// When <see cref="Promise" /> objects are re-materialized from the
    /// database, multiple instances may exist for the same key (<see cref="PromiseId" />)
    /// temporarily.  However, this dictionary can be used to drop all
    /// those instances but one.  A unique instance can be obtained from
    /// the return value of <see cref="SaveWeakReference(Promise)" />.
    /// </para>
    /// <para>
    /// Managing the weak references would have been much easier using locks,
    /// but looking up of promises by ID do need to be scalable as there may
    /// be many concurrent clients.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<PromiseId, object> _liveObjects = new();

    /// <summary>
    /// Pooled instances of the weak reference objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Weak reference objects are heavier than desired because they have
    /// finalizers, so they are pooled when possible.
    /// </para>
    /// <para>
    /// Unfortunately, Microsoft.Extensions.ObjectPool cannot be used because 
    /// it requires the class of the pooled objects to have a parameterless constructor.
    /// </para>
    /// <para>
    /// <see cref="ConcurrentQueue{T}"/> seems to be the fastest 
    /// standard thread-safe collection available for the access patterns used
    /// here: a weak reference object is usually recycled from a different
    /// thread than the one that re-takes it eventually.
    /// </para>
    /// </remarks>
    private readonly ConcurrentQueue<WeakReference<Promise>> _weakRefPool = new();

    /// <summary>
    /// Count of the total number of items inside <see cref="_weakRefPool" />.
    /// </summary>
    /// <remarks>
    /// This count is approximate in so far as the increment/decrement operations
    /// may race.  That is still good enough as the count is only used to 
    /// prevent retaining too many objects in the pool.
    /// </remarks>
    private int _weakRefPooledCount;

    /// <summary>
    /// (Soft) limit on the total number of items inside <see cref="_weakRefPool" />.
    /// </summary>
    private const int MaxWeakRefPooledCount = 16384;

    /// <summary>
    /// Get the (unique) in-memory promise object for the given ID
    /// if it already exists.
    /// </summary>
    /// <param name="id">
    /// The ID of the promise.
    /// </param>
    /// <returns>
    /// The <see cref="Promise" /> object if it exists in the 
    /// in-memory cache, or null if it does not.
    /// </returns>
    private Promise? TryGetLiveObject(PromiseId id)
    {
        if (!_liveObjects.TryGetValue(id, out object? v))
            return null;

        if (v is Promise promise)
            return promise;

        var weakRef = UnsafeCastToWeakReference(v);
        if (weakRef.TryGetTarget(out var promise2) && promise2.Id == id)
            return promise2;

        return null;
    }

    /// <summary>
    /// Store a weak reference to a promise object, demoting any existing
    /// strong reference.
    /// </summary>
    /// <param name="promise">
    /// The promise object that may be saved, associated to its ID.
    /// </param>
    /// <returns>
    /// The promise object that has been selected to represent the promise
    /// with the given ID.  This object is the same as 
    /// <paramref name="promise" /> if it is the
    /// first object seen since the last expiry of the cache entry.
    /// Otherwise, an existing object may be returned and a weak
    /// reference to that object gets saved instead into the cache.
    /// </returns>
    private Promise SaveWeakReference(Promise promise)
    {
        var id = promise.Id;
        var weakRef = CreateWeakReference(promise);

        // Retry loop for the rare occurrence that the loop body tries
        // to return an existing weak reference, but it expires midway.
        while (true)
        {
            // Update the cache entry for id to point to weakRef,
            // unless there is an existing strong or weak reference
            // that points to another Promise object.  In the latter case,
            // return the weak reference to that other Promise object
            // unless it has expired.
            //
            // If an existing weak reference object is being replaced,
            // we do not attempt to recycle it back to the pool.  It
            // is not easy to do so without introducing race conditions.
            // We prefer creating more garbage objects in uncommon cases
            // than to complicate the code even more.
            //
            // Removing an existing weak reference object first (with
            // _liveObjects.TryRemove) also does not work, as that can lead
            // to an "ABA" problem: the weak reference object may be recycled,
            // then re-used and stored into the same entry by a different thread,
            // so that this thread might erroneously remove a live entry!
            var otherWeakRef = UnsafeCastToWeakReference(_liveObjects.AddOrUpdate(
                key: id,
                addValueFactory: static object (id, arg) => arg.weakRef,
                updateValueFactory: object (id, old, arg) =>
                {
                    if (object.ReferenceEquals(old, arg.promise))
                    {
                        // Common case: demote a strong reference to
                        // a weak one when saving the promise's contents
                        return arg.weakRef;
                    }
                    else if (old is Promise p)
                    {
                        // Not common: there is a strong reference to
                        // a different Promise object (for the same ID)
                        return CreateWeakReference(p);
                    }
                    else
                    {
                        // Leave in existing weak reference unless it has expired
                        var oldWeakRef = UnsafeCastToWeakReference(old);
                        if (oldWeakRef.TryGetTarget(out var q) && q.Id == id)
                            return oldWeakRef;
                        else
                            return arg.weakRef;
                    }
                },
                factoryArgument: (weakRef, promise)));

            // Fast, common case: weakRef has been successfully stored.
            if (object.ReferenceEquals(otherWeakRef, weakRef))
                return promise;

            // Less common case: otherWeakRef, an existing weak reference,
            // is to be returned, and it has not expired in between when
            // it was checked inside updateValueFactory above and here.
            if (otherWeakRef.TryGetTarget(out var q) && q.Id == id)
            {
                // Our speculatively-created weakRef has not been exposed
                // outside, so it may be safely discarded here.
                weakRef.SetTarget(null!);
                DiscardWeakReference(weakRef);

                return q;
            }
        }
    }

    /// <summary>
    /// Casts a value from <see cref="_liveObjects"/> 
    /// to <see cref="WeakReference{T}"/> of <see cref="Promise"/>, assuming
    /// it is not an instance of <see cref="Promise" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WeakReference<Promise> UnsafeCastToWeakReference(object obj)
    {
#if DEBUG
        // Catch bugs if we cast wrongly
        return (WeakReference<Promise>)obj;
#else
        return Unsafe.As<WeakReference<Promise>>(obj);
#endif
    }

    /// <summary>
    /// Obtain a weak reference to a given promise object.
    /// </summary>
    /// <remarks>
    /// The weak reference object may be pooled.  Returning it to the
    /// pool is not mandatory.  For example, if a weak reference
    /// object is speculatively created, and a (rare) conflict 
    /// (with another thread) occurs when storing into the cache of 
    /// live objects, the weak reference object can just be dropped 
    /// on the floor.  .NET's normal garbage collection will take care of it.
    /// </remarks>
    /// <param name="promise">
    /// The promise that the weak reference object should point to.
    /// </param>
    /// <returns>
    /// A weak reference object taken from a pool, or newly constructed.
    /// </returns>
    private WeakReference<Promise> CreateWeakReference(Promise promise)
    {
        if (_weakRefPool.TryDequeue(out var weakRef))
        {
            Interlocked.Decrement(ref _weakRefPooledCount);
            weakRef.SetTarget(promise);
        }
        else
        {
            weakRef = new WeakReference<Promise>(promise);
        }

        return weakRef;
    }

    /// <summary>
    /// Discard a weak reference object, possibly putting it back into the pool.
    /// </summary>
    /// <param name="weakRef">
    /// The weak reference object, whose target should be currently nothing.
    /// </param>
    private void DiscardWeakReference(WeakReference<Promise> weakRef)
    {
        if (_weakRefPooledCount > MaxWeakRefPooledCount)
            return;

        Interlocked.Increment(ref _weakRefPooledCount);
        _weakRefPool.Enqueue(weakRef);
    }

    /// <summary>
    /// Store a new entry in the cache of live objects.
    /// </summary>
    /// <param name="promise">
    /// The promise object to be saved down.
    /// </param>
    /// <param name="isWeak">
    /// True to store a weak reference to <paramref name="promise"/>; 
    /// false to store a strong reference.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// An entry with the same ID as <paramref name="promise" />
    /// already exists in the cache of live objects.
    /// </exception>
    private void SaveNewReference(Promise promise, bool isWeak)
    {
        object v = isWeak ? CreateWeakReference(promise) : promise;
        if (!_liveObjects.TryAdd(promise.Id, v))
            throw new InvalidOperationException($"An entry for a promise with ID {promise.Id} already exists in the cache of live objects but was not expected to. ");
    }

    /// <summary>
    /// Scan the cache of live objects and delete entries
    /// that are expired weak references.
    /// </summary>
    private void CleanExpiredWeakReferences()
    {
        try
        {
            int totalEntries = 0;
            int cleanedEntries = 0;

            foreach (var (id, obj) in _liveObjects)
            {
                ++totalEntries;

                // Do not remove strong references.
                if (obj is Promise)
                    continue;

                // Weak reference has not expired yet
                var weakRef = UnsafeCastToWeakReference(obj);
                if (weakRef.TryGetTarget(out _))
                    continue;

                // Remove the expired weak reference.
                //
                // Do nothing if another thread races and substitutes
                // another value in the same entry.
                if (!_liveObjects.TryRemove(new KeyValuePair<PromiseId, object>(id, weakRef)))
                    continue;

                DiscardWeakReference(weakRef);

                ++cleanedEntries;
            }

            LogCleanedWeakReferences(_logger, cleanedEntries, totalEntries);
        }
        catch (Exception e)
        {
            _logger.LogCritical(e,
                "An error occurred while cleaning up entries for expired in-memory promises. ");
        }
        finally
        {
            _hasActivatedCleanUp = 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug,
                   Message = "Removed {cleaned} expired entries for in-memory promises out of {total} total entries. ")]
    private static partial void LogCleanedWeakReferences(ILogger logger, int cleaned, int total);
}
