using FASTER.core;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hearty.Server.FasterKV;

/// <summary>
/// Storage of promises for the Hearty job server which is backed
/// by a FASTER KV database.
/// </summary>
/// <remarks>
/// <para>
/// The FASTER KV database can store its data in files.
/// So promise data can exceed the amount of in-process (GC)
/// memory available, and can persist when the job server
/// restarts.
/// </para>
/// <para>
/// The database only stores complete promises.  Incomplete
/// promises always require an in-memory representation
/// as <see cref="Promise" /> objects so that they can receive
/// (asynchronous) results posted to them.
/// </para>
/// <para>
/// Promise objects are pushed out of memory when the garbage
/// collector sees they are not in use.  If they are retrieved
/// again, they are re-materialized as objects from their
/// serialized form in the FASTER KV database.
/// </para>
/// </remarks>
public sealed partial class FasterDbPromiseStorage 
    : PromiseStorage, IPromiseDataFixtures, IDisposable
{
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
    /// Prepare to store promises in memory and in the database.
    /// </summary>
    /// <param name="schemas">
    /// Data schemas required to re-materialize (de-serialize) promises
    /// from the database.
    /// </param>
    /// <param name="fileOptions">
    /// Options for the FASTER KV database.
    /// </param>
    public FasterDbPromiseStorage(PromiseDataSchemas schemas,
                                  in FasterDbFileOptions fileOptions)
    {
        _schemas = schemas;

        var logSettings = fileOptions.CreateFasterDbLogSettings();
        try
        {
            _device = logSettings.LogDevice;

            var indexSize =
                Math.Min(1L << 40, Math.Max(fileOptions.HashIndexSize, 256));

            _functions = new FunctionsImpl(this);
            var blobHooks = new PromiseBlobVarLenStruct();

            _sessionPool = new(new SessionPoolHooks(this));

            _db = new FasterKV<PromiseId, PromiseBlob>(
                    indexSize,
                    logSettings,
                    checkpointSettings: null,
                    comparer: new FasterDbPromiseComparer(),
                    variableLengthStructSettings: new()
                    {
                        valueLength = blobHooks
                    });

            _sessionVarLenSettings = new()
            {
                valueLength = blobHooks
            };
        }
        catch
        {
            _db?.Dispose();
            _device?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Limit on the number of bytes that serializing a promise results in.
    /// </summary>
    private const int MaxSerializationLength = (1 << 24);

    private readonly PromiseDataSchemas _schemas;

    PromiseStorage IPromiseDataFixtures.PromiseStorage => this;

    PromiseDataSchemas IPromiseDataFixtures.Schemas => _schemas;

    /// <inheritdoc />
    public override Promise CreatePromise(PromiseData? input, PromiseData? output = null)
    {
        var promise = CreatePromiseObject(input, output);

        bool canSerialize = promise.TryPrepareSerialization(out var info) &&
                            info.TotalLength <= MaxSerializationLength;

        if (canSerialize)
        {
            if (!DbTryAddValue(promise.Id, info))
            {
                throw new InvalidOperationException(
                    "The promise already exists in the database. This should not happen. ");
            }
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

    /// <inheritdoc />
    public override Promise? GetPromiseById(PromiseId id)
    {
        GCHandle gcHandle;
        Promise? promise;

        // Get the live .NET object if it exists.
        if (_objects.TryGetValue(id, out gcHandle))
        {
            promise = Unsafe.As<Promise?>(gcHandle.Target);
            if (promise is not null)
                return promise;
        }

        // Otherwise, try getting from the database.
        // If it exists, de-serialize the data and then register
        // the live .NET object.
        promise = DbTryGetValue(id);
        if (promise is not null)
        {
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

    /// <inheritdoc />
    public override void SchedulePromiseExpiry(Promise promise, DateTime expiry)
    {
        throw new NotImplementedException();
    }
}
