using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Server.FasterKV;

// "Missing XML comment for publicly visible type or member"
// Disabled until this library becomes less experimental
#pragma warning disable CS1591

public class FasterDbPromiseStorage : PromiseStorage, IDisposable
{
    private readonly FasterDbDictionary<PromiseId, ReadOnlyMemory<byte>> _db;

    private readonly ConcurrentDictionary<PromiseId, GCHandle> _objects = new();

    public FasterDbPromiseStorage()
    {
        _db = new(new FasterDbFileOptions
        {
            Path = string.Empty,
            DeleteOnDispose = true,
            Preallocate = true
        });
    }

    /// <inheritdoc />
    public override Promise CreatePromise(PromiseData? input, PromiseData? output = null)
    {
        var promise = CreatePromiseObject(input, output);

        var gcHandle = GCHandle.Alloc(promise);
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
        if (_db.TryGetValue(id, out var data))
        {
            var promise = DeserializePromise(data);
            gcHandle = GCHandle.Alloc(promise);

            Promise? promiseLast = null;
            try
            {
                // Retry loop for rare occurrence another thread tries to
                // update, and also its Promise object expires soon after.
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

    private Promise DeserializePromise(ReadOnlyMemory<byte> data)
    {
        // FIXME
        return null!;
    }

    public override void SchedulePromiseExpiry(Promise promise, DateTime expiry)
    {
        throw new NotImplementedException();
    }
}
