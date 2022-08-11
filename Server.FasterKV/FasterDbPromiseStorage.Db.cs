using FASTER.core;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.FasterKV;

public sealed partial class FasterDbPromiseStorage
{
    /// <summary>
    /// Promises represented in serialized form in the FASTER KV database.
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
    /// <para>
    /// The variable-length blobs for the promises are stored using
    /// <see cref="PromiseBlob" />.  FASTER KV comes with a blob type
    /// that it considers its standard, but it has a nasty unsafe API
    /// that is also rather poorly documented.  We prefer our own
    /// "blob" wrapper which is optimized for promise serialization
    /// and is as safe as possible.
    /// </para>
    /// </remarks>
    private readonly FasterKV<PromiseId, PromiseBlob> _db;

    /// <summary>
    /// Backing secondary storage used by FASTER KV which must be disposed.
    /// </summary>
    private readonly IDevice _device;

    /// <summary>
    /// Callbacks invoked by FASTER KV for <see cref="_db" />.
    /// </summary>
    private readonly FunctionsImpl _functions;

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        _db.Dispose();
        _device.Dispose();
    }

    /// <summary>
    /// The input passed into FASTER KV for RMW operations (including "TryAdd").
    /// </summary>
    private struct DbInput
    {
        /// <summary>
        /// Prepared serialization of the promise to store into the database.
        /// </summary>
        public PromiseSerializationInfo Serialization;
    }

    /// <summary>
    /// Callbacks invoked by FASTER KV required for <see cref="FasterDbPromiseStorage" />
    /// to implement its operations correctly and efficiently.
    /// </summary>
    private sealed class FunctionsImpl : FunctionsBase<PromiseId, PromiseBlob, DbInput, Promise?, Empty>
    {
        /// <summary>
        /// Needed to re-materialize promise objects from their blobs
        /// in the FASTER KV database.
        /// </summary>
        private readonly IPromiseDataFixtures _fixtures;

        public FunctionsImpl(IPromiseDataFixtures fixtures)
        {
            _fixtures = fixtures;
        }

        public override bool SingleReader(ref PromiseId key, ref DbInput input, ref PromiseBlob value, ref Promise? output, ref ReadInfo readInfo)
        {
            output = value.RestoreObject(_fixtures);
            return true;
        }

        public override bool ConcurrentReader(ref PromiseId key, ref DbInput input, ref PromiseBlob value, ref Promise? output, ref ReadInfo readInfo)
            => SingleReader(ref key, ref input, ref value, ref output, ref readInfo);

        /// <summary>
        /// Called by FASTER KV when inserting new data, updating data to a new slot, or when copying
        /// data to a new slot.
        /// </summary>
        public override bool SingleWriter(ref PromiseId key, ref DbInput input, ref PromiseBlob src, ref PromiseBlob dst, ref Promise? output, ref UpsertInfo upsertInfo, WriteReason writeReason)
        {
            if (writeReason == WriteReason.Upsert)
                dst.SaveSerialization(input.Serialization);
            else
                src.CopyTo(ref dst);
                
            return true;
        }

        /// <summary>
        /// Disallow in-place modifications for upsert.
        /// </summary>
        /// <remarks>
        /// FASTER FV calls this method when upsert finds an existing entry in its mutable region.
        /// By returning false, FASTER KV is forced to always allocate a new entry.
        /// </remarks>
        public override bool ConcurrentWriter(ref PromiseId key, ref DbInput input, ref PromiseBlob src, ref PromiseBlob dst, ref Promise? output, ref UpsertInfo upsertInfo)
            => false;

        /// <summary>
        /// Disallows in-place updating.
        /// entries. 
        /// </summary>
        /// <remarks>
        /// FASTER KV seems to call this optimistically, but as usual
        /// it is poorly documented.  It seems that this method must
        /// return a result consistent with <see cref="NeedCopyUpdate" />.
        /// </remarks>
        public override bool InPlaceUpdater(ref PromiseId key, ref DbInput input, ref PromiseBlob value, ref Promise? output, ref RMWInfo rmwInfo)
            => throw new NotImplementedException();

        /// <summary>
        /// Always requiring new allocations to update an entry,
        /// both because the new entry may change the size and also reading does
        /// not tolerate concurrent modifications.
        /// </summary>
        public override bool NeedCopyUpdate(ref PromiseId key, ref DbInput input, ref PromiseBlob oldValue, ref Promise? output, ref RMWInfo rmwInfo)
            => throw new NotImplementedException();

        /// <summary>
        /// Called by FASTER KV for creating a entry to replace an existing one.
        /// </summary>
        public override bool CopyUpdater(ref PromiseId key, ref DbInput input, ref PromiseBlob oldValue, ref PromiseBlob newValue, ref Promise? output, ref RMWInfo rmwInfo)
            => throw new NotImplementedException();

        /// <summary>
        /// Called by FASTER KV for creating a new entry.
        /// </summary>
        public override bool InitialUpdater(ref PromiseId key, ref DbInput input, ref PromiseBlob value, ref Promise? output, ref RMWInfo rmwInfo)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Hooks for FASTER KV to work with 
    /// <see cref="PromiseBlob" /> as a variable-length structure.
    /// </summary>
    private sealed class PromiseBlobVarLenStruct
        : IVariableLengthStruct<PromiseBlob>
        , IVariableLengthStruct<PromiseBlob, DbInput>
    {
        #region IVariableLengthStruct<PromiseBlob>

        /// <summary>
        /// Get an "average" length of a blob used to size the 
        /// read request for it when FASTER KV needs to read from
        /// secondary storage.
        /// </summary>
        /// <returns>
        /// An expected length of the blob.  Currently hard-coded
        /// to 1 kilobyte.  This number is required by FASTER KV
        /// to include the header information so the actual size
        /// of the blob can be determined after reading this number
        /// of bytes of it.
        /// </returns>
        public int GetInitialLength() => 1024;

        /// <summary>
        /// Get the length, in bytes, of a promise 
        /// blob that has already been initialized.
        /// </summary>
        /// <param name="t">
        /// Reference to the blob.
        /// </param>
        /// <returns>
        /// The length of the blob as reported by its header
        /// (assumed to be correctly initialized).
        /// </returns>
        public int GetLength(ref PromiseBlob t) => t.TotalLength;

        /// <summary>
        /// Called by FASTER KV when it needs to internally 
        /// copy a blob to another location, e.g. for asynchronous operations.
        /// </summary>
        /// <param name="source">
        /// Reference to the blob to copy.
        /// </param>
        /// <param name="destination">
        /// Pointer to the memory to copy to, assumed to be sized correctly.
        /// </param>
        public unsafe void Serialize(ref PromiseBlob source, void* destination)
            => source.CopyTo(destination);

        /// <summary>
        /// Called by FASTER KV to cast an already allocated and initialized
        /// piece of memory to a reference to <see cref="PromiseBlob" />.
        /// </summary>
        /// <param name="source">
        /// Pointer to the beginning byte of memory for the blob.
        /// </param>
        /// <returns>
        /// <paramref name="source"/> re-interpreted as a reference
        /// to the promise blob.
        /// </returns>
        public unsafe ref PromiseBlob AsRef(void* source)
            => ref Unsafe.AsRef<PromiseBlob>(source);

        /// <summary>
        /// Called by FASTER KV to initialize a <see cref="PromiseBlob" />
        /// on top of memory it has just allocated.
        /// </summary>
        /// <param name="source">
        /// Pointer to the beginning byte of memory.
        /// </param>
        /// <param name="end">
        /// Pointer to one past the last byte of memory.
        /// </param>
        public unsafe void Initialize(void* source, void* end)
        {
            int length = checked((int)((byte*)end - (byte*)source));
            PromiseBlob.Initialize(new Span<byte>(source, length));
        }

        #endregion

        #region IVariableLengthStruct<PromiseBlob, PromiseSerializationInfo>

        /// <summary>
        /// Called by FASTER KV to get the length of the blob when initially
        /// allocating for an RMW operation.
        /// </summary>
        /// <param name="input">
        /// Input to the RMW operation.
        /// </param>
        /// <returns>
        /// Total number of bytes required for storing the serialized form
        /// of the entry to write for RMW.
        /// </returns>
        public int GetInitialLength(ref DbInput input)
            => (int)input.Serialization.TotalLength;

        /// <summary>
        /// Called by FASTER KV to get the length of the blob when re-allocating
        /// an existing entry for an RMW operation.
        /// </summary>
        /// <param name="t">
        /// Existing blob stored in the database.
        /// </param>
        /// <param name="input">
        /// Input to the RMW operation.
        /// </param>
        /// <returns>
        /// The number of bytes required for the new blob.
        /// </returns>
        public int GetLength(ref PromiseBlob t, ref DbInput input)
            => (int)input.Serialization.TotalLength;

        #endregion
    }

    /// <summary>
    /// The thread-local "session" for invoking FASTER KV operations.
    /// </summary>
    private struct LocalSession : IDisposable
    {
        public readonly ClientSession<PromiseId, PromiseBlob,
                                      DbInput, Promise?,
                                      Empty, FunctionsImpl> Session;

        public LocalSession(FasterDbPromiseStorage parent)
        {
            Session = parent._db
                            .For(parent._functions)
                            .NewSession<FunctionsImpl>(
                                sessionName: null,
                                parent._sessionVarLenSettings);
        }

        public void Dispose() => Session.Dispose();
    }

    private readonly SessionVariableLengthStructSettings<PromiseBlob, DbInput>
        _sessionVarLenSettings;

    /// <summary>
    /// Thread-local cache of FASTER KV sessions to avoid repeated allocation
    /// while avoiding lock contention.
    /// </summary>
    private ThreadLocalObjectPool<LocalSession, SessionPoolHooks> _sessionPool;

    /// <summary>
    /// Hooks for FASTER KV sessions to be managed by <see cref="_sessionPool" />.
    /// </summary>
    private struct SessionPoolHooks : IThreadLocalObjectPoolHooks<LocalSession, SessionPoolHooks>
    {
        private readonly FasterDbPromiseStorage _parent;

        public ref ThreadLocalObjectPool<LocalSession, SessionPoolHooks>
            Root => ref _parent._sessionPool;

        public LocalSession InstantiateObject() => new LocalSession(_parent);

        public SessionPoolHooks(FasterDbPromiseStorage parent)
            => _parent = parent;
    }

    /// <summary>
    /// Instantiate the promise object by de-serializing from the corresponding
    /// blob in the FASTER KV database, if it exists.
    /// </summary>
    /// <returns>
    /// The re-materialized promise object, 
    /// or null if it has no entry in the FASTER KV database.
    /// </returns>
    private Promise? DbTryGetValue(PromiseId key)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        var session = pooledSession.Target.Session;

        // session.Read can return "pending" if I/O to secondary storage
        // is required.  Using ReadAsync avoids that complication.
        (Status status, Promise? promise) = session.ReadAsync(ref key)
                                                   .Wait()
                                                   .Complete();

        if (status.IsFaulted)
            throw new FasterException("Failed to request an item from a promise storage backed by FASTER KV. ");

        return promise;
    }

    /// <summary>
    /// Add or set an entry for the serialization of a promise 
    /// as a blob in the FASTER KV database.
    /// </summary>
    /// <returns>True if the blob has been successfully added;
    /// false if an existing entry was changed.
    /// </returns>
    private bool DbSetValue(PromiseId key, in PromiseSerializationInfo serialization)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        var session = pooledSession.Target.Session;

        var input = new DbInput { Serialization = serialization };
        PromiseBlob dummyValue = PromiseBlob.CreatePlaceholder(checked((int)serialization.TotalLength));
        Promise? output = null; // unused;

        // Unlike reads, upserts in FASTER KV cannot return "pending"
        // (see https://github.com/microsoft/FASTER/issues/355#issuecomment-713199732)
        // so can safely call this synchronous method.
        Status status = session.Upsert(ref key, ref input, ref dummyValue, ref output);

        if (status.IsFaulted)
            throw new FasterException("Failed to an item to a promise storage backed by FASTER KV. ");

        return status.NotFound;
    }

    /// <summary>
    /// Get the number of entries in the FASTER KV database.
    /// </summary>
    /// <remarks>
    /// Currently, getting this count requires scanning the entire
    /// hash index.  It is not a fast O(1) operation.
    /// </remarks>
    public long GetDatabaseEntriesCount() => _db.EntryCount;

    /// <summary>
    /// The number of (generation 0) garbage collections 
    /// that have occurred between the start of the this process
    /// and the last cleaning up of expired GC handles.
    /// </summary>
    private int _lastGarbageCollectionCount;

    /// <summary>
    /// The last time that cleaning of GC handles might be triggered.
    /// </summary>
    private long _lastCleanUpCheckTime;

    /// <summary>
    /// Flipped on (to value 1) if cleaning of GC handles has been scheduled.
    /// </summary>
    private int _hasActivatedCleanUp;

    /// <summary>
    /// Heuristically check if the objects dictionary should be scanned
    /// for expired GC handles to remove.
    /// </summary>
    /// <remarks>
    /// We use the same technique as the "MemoryCache" class;
    /// we poll for when cleaning is needed, and if so, 
    /// schedule a background task for it.
    /// </remarks>
    private void ScheduleCleaningIfNeeded()
    {
        unchecked
        {
            var now = Environment.TickCount64;

            if (now - _lastCleanUpCheckTime <= 1000)
                return;

            // If the above check passes, write out the updated time
            // immediately to avoid the more expensive checks below
            // when this method is invoked again soon.
            //
            // On 32-bit .NET, this read can tear but we can tolerate
            // the above check returning the wrong result very rarely.
            _lastCleanUpCheckTime = now;

            // May be an expensive query in the .NET run-time system.
            var count = GC.CollectionCount(0);
            if (count - _lastGarbageCollectionCount <= 0)
                return;

            if (Interlocked.Exchange(ref _hasActivatedCleanUp, 1) != 0)
                return;

            _lastGarbageCollectionCount = count;

            Task.Factory.StartNew(state => ((FasterDbPromiseStorage)state!).CleanExpiredWeakReferences(),
                                  this,
                                  CancellationToken.None,
                                  TaskCreationOptions.DenyChildAttach,
                                  TaskScheduler.Default);
        }
    }
}
