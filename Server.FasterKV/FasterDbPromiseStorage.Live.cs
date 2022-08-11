using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

        while (true)
        {
            // Set the entry's value to be a weak reference unless it already is one.
            var weakRef = UnsafeCastToWeakReference(_liveObjects.AddOrUpdate(
                                key: id,
                                addValueFactory: (id, arg) 
                                    => CreateWeakReference(arg),
                                updateValueFactory: (id, old, arg)
                                    => old is Promise p ? CreateWeakReference(p) : old,
                                factoryArgument: promise));

            // Return the promise object held by the weak reference.
            //
            // If the weak reference is existing, has expired, and
            // another thread raced to re-use it to hold another
            // promise, we need to retry the whole operation.
            if (weakRef.TryGetTarget(out var newPromise))
            {
                if (newPromise.Id == id)
                    return newPromise;
            }

            // If the weak reference is existing but has expired, 
            // attempt to clean it up, and then retry the whole operation.
            // If another thread races to clean up the weak reference,
            // we can safely ignore it.
            else
            {
                if (_liveObjects.TryRemove(new KeyValuePair<PromiseId, object>(id, weakRef)))
                    DiscardWeakReference(weakRef);
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

    private WeakReference<Promise> CreateWeakReference(Promise promise)
        => new WeakReference<Promise>(promise);

    private void DiscardWeakReference(WeakReference<Promise> weakRef) { }

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

            _logger.LogDebug("Removed {cleaned} expired entries for in-memory promises out of {total} total entries. ",
                             cleanedEntries, totalEntries);
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
}
