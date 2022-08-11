using FASTER.core;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

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
    /// <param name="logger">
    /// Logs significant events regarding this database,
    /// and critical errors from <see cref="Promise" /> objects.
    /// </param>
    /// <param name="schemas">
    /// Data schemas required to re-materialize (de-serialize) promises
    /// from the database.
    /// </param>
    /// <param name="fileOptions">
    /// Options for the FASTER KV database.
    /// </param>
    public FasterDbPromiseStorage(ILogger<FasterDbPromiseStorage> logger,
                                  PromiseDataSchemas schemas,
                                  in FasterDbFileOptions fileOptions)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        _schemas = schemas;
        _promiseUpdateEventHandler = this.PromiseHasUpdated;

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
                    },
                    disableLocking: true);

            _sessionVarLenSettings = new()
            {
                valueLength = blobHooks
            };

            _lastCleanUpCheckTime = Environment.TickCount64;
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

    ILogger IPromiseDataFixtures.Logger => _logger;

    /// <inheritdoc />
    public override Promise CreatePromise(PromiseData? input, PromiseData? output = null)
    {
        var promise = CreatePromiseObject(input, output);

        bool canSerialize = promise.TryPrepareSerialization(out var info) &&
                            info.TotalLength <= MaxSerializationLength;

        if (canSerialize)
            DbSetValue(promise.Id, info);

        SaveNewReference(promise, canSerialize);

        if (!promise.HasCompleteOutput)
            promise.OnUpdate += _promiseUpdateEventHandler;

        ScheduleCleaningIfNeeded();

        return promise;
    }

    /// <summary>
    /// Pre-allocated event handler attached to all promises created from
    /// this storage provider.
    /// </summary>
    private readonly EventHandler<Promise.UpdateEventArgs> _promiseUpdateEventHandler;

    /// <summary>
    /// Called when <see cref="Promise.OnUpdate" /> fires,
    /// to save updated results to the database.
    /// </summary>
    /// <remarks>
    /// The serialization and saving of the results is deferred to a background
    /// task.
    /// </remarks>
    private void PromiseHasUpdated(object? sender, Promise.UpdateEventArgs args)
    {
        Task.Factory.StartNew(o =>
        {
            var promise = Unsafe.As<Promise>(o!);
            var id = promise.Id;

            try
            {
                bool canSerialize = promise.TryPrepareSerialization(out var info) &&
                                    info.TotalLength <= MaxSerializationLength;

                if (canSerialize)
                {
                    LogPromiseSaved(_logger, id);
                    bool isAdded = DbSetValue(id, info);

                    // If this is the first time the promise is added to the
                    // database, demote to a weak reference in _objects.
                    if (isAdded)
                        SaveWeakReference(promise);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to save promise with ID {id}", id);
            }
        }, args.Subject);
    }

    /// <inheritdoc />
    public override Promise? GetPromiseById(PromiseId id)
    {
        bool materialized = false;
        var promise = TryGetLiveObject(id);

        if (promise is null)
        {
            ScheduleCleaningIfNeeded();

            // Otherwise, try getting from the database.
            // If it exists, de-serialize the data and then register
            // the live .NET object.
            promise = DbTryGetValue(id);
            if (promise is null)
            {
                LogMissingPromise(_logger, id);
                return null;
            }

            materialized = true;
            promise = SaveWeakReference(promise);
            promise.OnUpdate += _promiseUpdateEventHandler;
        }

        LogPromiseRetrieved(_logger, id, wasLive: !materialized);
        return promise;
    }

    /// <inheritdoc />
    public override void SchedulePromiseExpiry(Promise promise, DateTime expiry)
    {
        throw new NotImplementedException();
    }

    [LoggerMessage(Level = LogLevel.Debug,
                   Message = "Retrieved promise with ID {id}, was live (cached in memory) = {wasLive}")]
    private static partial void LogPromiseRetrieved(ILogger logger, PromiseId id, bool wasLive);

    [LoggerMessage(Level = LogLevel.Information,
                   Message = "Attempt to retrieved promise with ID {id} which does not currently exist")]
    private static partial void LogMissingPromise(ILogger logger, PromiseId id);

    [LoggerMessage(Level = LogLevel.Debug,
                   Message = "Saving contents of promise with ID {id} to the database")]
    private static partial void LogPromiseSaved(ILogger logger, PromiseId id);
}
