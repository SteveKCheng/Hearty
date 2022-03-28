using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hearty.Server.FasterKV;

// "Missing XML comment for publicly visible type or member"
// Disabled until this library becomes less experimental
#pragma warning disable CS1591

public sealed class FasterDbPromiseStorage : PromiseStorage, IDisposable
{
    /// <summary>
    /// Promises represented in serialized form in the Faster KV database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For performance reasons, <see cref="Promise" /> does not have a finalizer.
    /// So it is not possible to serialize a promise object only when it is about
    /// to be collected as garbage; the serialized form must be present in
    /// the database at the same time as the live object in <see cref="_objects" />.
    /// However, for persistence and disaster recovery, that is going to
    /// be required anyway.
    /// </para>
    /// </remarks>
    private readonly FasterDbDictionary<PromiseId, ReadOnlyMemory<byte>> _db;

    /// <summary>
    /// Promises that may have a current live representation as .NET objects.
    /// </summary>
    /// <remarks>
    /// Promise objects that become garbage will have their 
    /// <see cref="GCHandle" /> set to null.  The null entry will
    /// get cleaned up periodically or the next time it is
    /// accessed.
    /// </remarks>
    private readonly ConcurrentDictionary<PromiseId, GCHandle> _objects = new();

    /// <summary>
    /// Needed to re-materialize promise objects from <see cref="_db" />.
    /// </summary>
    private readonly PromiseDataSchemas _schemas;

    public FasterDbPromiseStorage(PromiseDataSchemas schemas)
    {
        _schemas = schemas;
        _db = new(new FasterDbFileOptions
        {
            Path = string.Empty,
            DeleteOnDispose = true,
            Preallocate = true
        });
    }

    /// <summary>
    /// Limit on the number of bytes that serializing a promise results in.
    /// </summary>
    private const int MaxSerializationLength = (1 << 24);

    /// <inheritdoc />
    public override Promise CreatePromise(PromiseData? input, PromiseData? output = null)
    {
        var promise = CreatePromiseObject(input, output);

        bool canSerialize = promise.TryPrepareSerialization(out var info) &&
                            info.TotalLength <= MaxSerializationLength;

        if (canSerialize)
        {
            // FIXME: have FASTER KV write to database page directly to avoid copying bytes
            byte[] bytes = new byte[info.TotalLength];
            info.Serialize(bytes);
            _db.Add(promise.Id, new ReadOnlyMemory<byte>(bytes));
        }

        var gcHandle = GCHandle.Alloc(promise, canSerialize ? GCHandleType.Weak
                                                            : GCHandleType.Normal);
        try
        {
            if (!_objects.TryAdd(promise.Id, gcHandle))
                throw new InvalidOperationException("The promise with the newly generated ID already exists.  This should not happen. ");
        }
        catch
        {
            gcHandle.Free();
            throw;
        }

        return promise;
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        _db.Dispose();
    }

    /// <inheritdoc />
    public override Promise? GetPromiseById(PromiseId id)
    {
        GCHandle gcHandle;

        // Get the live .NET object if it exists.
        if (_objects.TryGetValue(id, out gcHandle))
        {
            var promise = Unsafe.As<Promise?>(gcHandle.Target);
            if (promise is not null)
                return promise;
        }

        // Otherwise, try getting from the database.
        // If it exists, de-serialize the data and then register
        // the live .NET object.
        //
        // FIXME: Also try to avoid double copying here
        if (_db.TryGetValue(id, out var data))
        {
            var promise = Promise.Deserialize(_schemas, data);
            gcHandle = GCHandle.Alloc(promise, GCHandleType.Weak);

            Promise? promiseLast = null;
            try
            {
                // Retry loop for the rare occurrence when another thread tries
                // to update, and also its Promise object expires soon after.
                do
                {
                    var gcHandleLast = _objects.AddOrUpdate(
                        id,
                        static (_, newValue) => newValue,
                        static (_, oldValue, newValue)
                            => oldValue.Target is null ? newValue : oldValue,
                        gcHandle);

                    promiseLast = Unsafe.As<Promise?>(gcHandleLast.Target);
                } while (promiseLast is null);
            }
            finally
            {
                if (!object.ReferenceEquals(promise, promiseLast))
                    gcHandle.Free();
            }

            return promiseLast;
        }

        return null;
    }

    public override void SchedulePromiseExpiry(Promise promise, DateTime expiry)
    {
        throw new NotImplementedException();
    }
}
