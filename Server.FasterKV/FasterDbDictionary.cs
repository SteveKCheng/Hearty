using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using FASTER.core;

namespace Hearty.Server.FasterKV;

// "Missing XML comment for publicly visible type or member"
// Disabled until this library becomes less experimental
#pragma warning disable CS1591

/// <summary>
/// A function that produces a desired value to add 
/// into <see cref="FasterDbDictionary{TKey, TValue}" />.
/// </summary>
/// <remarks>
/// Such a function is used to avoid the inefficiency in 
/// re-materializing the value when the key already exists
/// in the dictionary.
/// </remarks>
/// <typeparam name="TState">
/// Arbitrary state that the factory function can read from and write to.
/// </typeparam>
/// <typeparam name="TKey">
/// The data type for the key of an item in the dictionary.
/// </typeparam>
/// <typeparam name="TValue">
/// The data type for the associated value of an item in the dictionary.
/// </typeparam>
/// <param name="state">
/// Reference to the state for the factory function.
/// </param>
/// <param name="key">
/// The key for the item to be produced.
/// </param>
/// <returns>
/// The desired value for the item.
/// </returns>
public delegate TValue DictionaryItemFactory<TState, TKey, TValue>(ref TState state, in TKey key);

/// <summary>
/// Concurrent dictionary implemented in a FASTER KV database.
/// </summary>
/// <remarks>
/// <para>
/// FASTER KV can be used to persist the items into the filesystem,
/// thus allowing this dictionary to grow beyond the limits of
/// in-process GC memory. 
/// </para>
/// <para>
/// The API of FASTER KV is rather low-level and awkward.
/// This class specializes it to a application of storing
/// key-value pairs that are never partially updated.
/// </para>
/// <para>
/// For simplicity, this class does not support asynchronous
/// backing storage even though FASTER KV does.  When data needs
/// to be loaded from or saved to the filesystem, this class
/// simply synchronously blocks the calling thread.  In practice,
/// I/O on an SSD should be fast enough, and file I/O in .NET 
/// may not even be asynchronous in the first place. 
/// (e.g. on Linux, asynchronous file I/O is only implemented
/// by "io_uring" from recent Linux kernels, 
/// which the .NET run-time system is not using.)
/// </para>
/// <para>
/// In general, this class is intended to be a near drop-in replacement
/// for <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}" />.
/// </para>
/// <para>
/// Like in standard .NET dictionaries, <typeparamref name="TValue" /> 
/// is essentially treated immutable: modifying an existing entry
/// means replacing it.  (If <typeparamref name="TValue" /> is a reference type, 
/// then it is the object reference that gets replaced, but the .NET object
/// being pointed to can be arbitrarily mutated.)  In contrast, 
/// FASTER KV allows the value to be mutable structs with fields
/// that are updated with atomic (interlocked) instructions. 
/// </para>
/// <para>
/// A few methods from <see cref="IDictionary{TKey, TValue}" />, in particular:
/// <list type="bullet">
/// <item><see cref="ICollection{T}.Remove(T)" /></item>
/// <item><see cref="ICollection{T}.Clear"/> </item>
/// </list>
/// are not supported but they cannot be implemented efficiently
/// and correctly (for concurrent callers) by FASTER KV.
/// </para>
/// </remarks>
/// <typeparam name="TKey">
/// The data type for the key in the dictionary.
/// </typeparam>
/// <typeparam name="TValue">
/// The data type for the values in the dictionary.
/// </typeparam>
public partial class FasterDbDictionary<TKey, TValue> 
    : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>, IDisposable
    where TKey : unmanaged
    where TValue : struct
{
    private readonly FunctionsImpl _functions = new();
    private readonly IDevice _device;
    private readonly FasterKV<TKey, TValue> _storage;
    private bool _isDisposed;

    /// <summary>
    /// Count of the number of items in the dictionary.
    /// </summary>
    /// <remarks>
    /// FASTER KV does not track this count separately.
    /// When initializing FASTER KV from an existing file,
    /// the file needs to be scanned to determine this count.
    /// Afterwards, this class maintains the count incrementally.
    /// </remarks>
    private long _itemsCount;

    /// <summary>
    /// Compares instances of <typeparamref name="TValue" /> 
    /// as required for certain methods.
    /// </summary>
    public IEqualityComparer<TValue> ValueComparer { get; set; }

    private unsafe struct DbInput
    {
        public TValue Value;
    }

    private struct DbOutput
    {
        public TValue Value;
    }

    /// <summary>
    /// Callbacks invoked by FASTER KV required for <see cref="FasterDbDictionary{TKey, TValue}" />
    /// to implement its operations correctly and efficiently.
    /// </summary>
    /// <remarks>
    /// Record-level locking in FASTER KV is turned off for efficiency:
    /// that works because <typeparamref name="TValue" /> is being treated as 
    /// essentially immutable.  Only complete instances of 
    /// <typeparamref name="TValue"/> are published into FASTER KV's hash 
    /// index, so readers may be concurrent with no risk of "struct tearing". 
    /// Existing entries cannot be modified in-place; a copy is forced and
    /// then the new value gets published.
    /// </remarks>
    private sealed class FunctionsImpl : FunctionsBase<TKey, TValue, DbInput, DbOutput, AsyncOutput>
    {
        public FunctionsImpl() : base(locking: false) { }

        public override void SingleReader(ref TKey key, ref DbInput input, ref TValue value, ref DbOutput output)
            => output.Value = value;

        public override void ConcurrentReader(ref TKey key, ref DbInput input, ref TValue value, ref DbOutput output)
            => output.Value = value;

        public override void SingleWriter(ref TKey key, ref TValue src, ref TValue dst)
            => dst = src;

        /// <summary>
        /// Disallow in-place modifications for upsert.
        /// </summary>
        /// <remarks>
        /// FASTER FV calls this method when upsert finds an existing entry in its mutable region.
        /// </remarks>
        public override bool ConcurrentWriter(ref TKey key, ref TValue src, ref TValue dst)
            => false;   

        /// <summary>
        /// Fake "RMW" operation for TryAdd, when it encounters existing entry.
        /// </summary>
        /// <remarks>
        /// The existing entry is not modified for "TryAdd"; this method only needs
        /// to copy the existing value out.
        /// </remarks>
        public override bool InPlaceUpdater(ref TKey key, ref DbInput input, ref TValue value, ref DbOutput output)
        {
            output.Value = value;
            return true;
        }

        /// <summary>
        /// Should not actually be called because <see cref="NeedCopyUpdate" /> always returns false.
        /// </summary>
        public override void CopyUpdater(ref TKey key, ref DbInput input, ref TValue oldValue, ref TValue newValue, ref DbOutput output)
        {
            output.Value = newValue = oldValue;
        }

        /// <summary>
        /// Turn off copy-update since the "RMW" operation does not actually modify
        /// any existing value.
        /// </summary>
        public override bool NeedCopyUpdate(ref TKey key, ref DbInput input, ref TValue oldValue, ref DbOutput output)
            => false;

        /// <summary>
        /// Fake "RMW" operation for TryAdd, when it is to (speculatively) create a new entry.
        /// </summary>
        public override unsafe void InitialUpdater(ref TKey key, ref DbInput input, ref TValue value, ref DbOutput output)
        {
            output.Value = value = input.Value;
        }

        /// <summary>
        /// Propagate the result of reads, after I/O completes.
        /// </summary>
        public override void ReadCompletionCallback(ref TKey key, ref DbInput input, ref DbOutput output, AsyncOutput asyncOutput, Status status)
            => asyncOutput.Copy(status, output);

        /// <summary>
        /// Propagate the result of the "RMW" operation, after I/O completes.
        /// </summary>
        public override void RMWCompletionCallback(ref TKey key, ref DbInput input, ref DbOutput output, AsyncOutput asyncOutput, Status status)
            => asyncOutput.Copy(status, output);
    }

    public FasterDbDictionary(in FasterDbFileOptions fileOptions,
                              IFasterEqualityComparer<TKey>? comparer = null,
                              VariableLengthStructSettings<TKey, TValue>? varLenSettings = null)
    {
        ValueComparer = EqualityComparer<TValue>.Default;

        IDevice? device = null;
        FasterKV<TKey, TValue>? storage = null;

        try
        {
            if (fileOptions.Path is null)
            {
                device = new NullDevice();
            }
            else
            {
                bool deleteOnClose = fileOptions.DeleteOnDispose;
                var path = fileOptions.Path;
                if (string.IsNullOrEmpty(path))
                {
                    path = Path.GetTempFileName();
                    deleteOnClose = true;
                }

                device = Devices.CreateLogDevice(
                        logPath: path,
                        preallocateFile: fileOptions.Preallocate,
                        deleteOnClose: deleteOnClose);
            }

            var logSettings = new LogSettings
            {
                LogDevice = device
            };
            logSettings.MemorySizeBits =
                Math.Min(47, Math.Max(logSettings.PageSizeBits,
                                      fileOptions.MemoryLog2Capacity));

            var indexSize =
                Math.Min(1L << 40, Math.Max(fileOptions.HashIndexSize, 256));

            storage = new FasterKV<TKey, TValue>(
                        indexSize,
                        logSettings,
                        checkpointSettings: null,
                        serializerSettings: null,
                        comparer,
                        varLenSettings);

            _itemsCount = storage.EntryCount;

            _sessionPool = new(new SessionPoolHooks(this));
        }
        catch
        {
            storage?.Dispose();
            device?.Dispose();
            throw;
        }

        _device = device;
        _storage = storage;
    }

    /// <summary>
    /// Add an item where the value is produced by the factory only
    /// if the key for the item does not already exist.
    /// </summary>
    /// <typeparam name="TState">
    /// Arbitrary state that the factory function can read from and write to.
    /// </typeparam>
    /// <param name="state">
    /// Reference to the state for the factory function.
    /// </param>
    /// <param name="key">
    /// The key for the item to add into the database.
    /// </param>
    /// <param name="factory">
    /// Function that is invoked to produce the item's value when
    /// the key does not already exist in the database.
    /// </param>
    /// <param name="storedValue">
    /// The value produced by the factory, or the existing one
    /// if the item with the same key already exists.
    /// </param>
    /// <returns>
    /// Whether the item has been added by the factory function.
    /// </returns>
    public unsafe bool TryAdd<TState>(in TKey key,
                                      ref TState state,
                                      DictionaryItemFactory<TState, TKey, TValue> factory,
                                      out TValue storedValue)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        var session = pooledSession.Target.Session;

        if (TryGetValueInternal(pooledSession.Target, key, out storedValue))
            return false;

        return TryAddInternal(pooledSession.Target, key, factory(ref state, key), out storedValue);
    }

    public bool TryAdd(in TKey key, in TValue value)
        => TryAdd(key, value, out _);

    private bool TryAddInternal(LocalSession localSession,
                                in TKey key,
                                in TValue desiredValue,
                                out TValue storedValue)
    {
        var session = localSession.Session;

        var input = new DbInput
        {
            Value = desiredValue
        };

        var output = new DbOutput();

        Status status = session.RMW(ref Unsafe.AsRef(key),
                                    ref input,
                                    ref output,
                                    localSession.AsyncOutput);

        if (status == Status.PENDING)
        {
            session.CompletePending(wait: true);
            (status, output) = localSession.AsyncOutput;
        }

        if (status == Status.ERROR)
            throw new Exception("Retrieving a key from Faster KV resulted in an error. ");

        storedValue = output.Value;

        bool hasAdded = (status == Status.NOTFOUND);
        if (hasAdded)
            Interlocked.Increment(ref _itemsCount);

        return hasAdded;
    }

    public bool TryAdd(in TKey key, in TValue value, out TValue storedValue)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        return TryAddInternal(pooledSession.Target, key, value, out storedValue);
    }

    /// <summary>
    /// Get or set the value associated with a key.
    /// </summary>
    /// <param name="key">
    /// The key of the dictionary entry to get or set.
    /// </param>
    /// <returns>
    /// The value associated with <paramref name="key" />.
    /// </returns>
    /// <exception cref="KeyNotFoundException">
    /// For retrieving the item with <paramref name="key" />, if that
    /// key is not found in the dictionary.
    /// </exception>
    public TValue this[TKey key]
    {
        get
        {
            if (TryGetValue(key, out var value))
                return value;

            throw new KeyNotFoundException($"The key {key} was not found in the dictionary. ");
        }
        set
        {
            using var pooledSession = _sessionPool.GetForCurrentThread();
            var session = pooledSession.Target.Session;

            Status status = session.Upsert(ref key, ref value);

            // Status.PENDING only occurs if the key already exists, due
            // to how FASTER KV works: the hash index is never off-loaded
            // to external storage.  We can let it complete in the background.
            // Note that we register no callback for Upsert, so AsyncOutput can
            // be freely written to for subsequent operations on this thread.

            bool hasAdded = (status == Status.NOTFOUND);
            if (hasAdded)
                Interlocked.Increment(ref _itemsCount);

            if (status == Status.ERROR)
                throw new Exception("Retrieving a key from Faster KV resulted in an error. ");
        }
    }

    /// <summary>
    /// Get the number of items stored in this database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This count is necessarily a snapshot; concurrent operations may 
    /// change it.
    /// </para>
    /// <para>
    /// If the database has more than <see cref="int.MaxValue" />,
    /// the value returned by this property is capped.
    /// Consult <see cref="LongCount" /> for the uncapped value.
    /// </para>
    /// </remarks>
    public int Count
    {
        get
        {
            var count = _itemsCount;
            return count <= int.MaxValue ? (int)count : int.MaxValue;
        }
    }

    /// <summary>
    /// Get the number of items stored in this database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This count is necessarily a snapshot; concurrent operations may 
    /// change it.
    /// </para>
    /// </remarks>
    public long LongCount => _itemsCount;

    /// <inheritdoc cref="ICollection{T}.IsReadOnly" />
    public bool IsReadOnly => false;

    /// <inheritdoc cref="IDictionary{TKey, TValue}.Add" />
    public void Add(TKey key, TValue value)
    {
        if (!TryAdd(key, value))
            throw new InvalidOperationException("The key already exists. ");
    }

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item)
        => Add(item.Key, item.Value);

    void ICollection<KeyValuePair<TKey, TValue>>.Clear()
        => throw new NotSupportedException();

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
        => TryGetValue(item.Key, out var value) && ValueComparer.Equals(value, item.Value);

    /// <inheritdoc cref="IDictionary{TKey, TValue}.ContainsKey(TKey)" />
    public bool ContainsKey(TKey key)
    {
        return TryGetValue(key, out _);
    }

    /// <inheritdoc cref="IDictionary{TKey, TValue}.Remove" />
    public bool Remove(TKey key)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        var session = pooledSession.Target.Session;

        Status status = session.Delete(ref key);

        // Status.PENDING only occurs if the key already exists, due
        // to how FASTER KV works: the hash index is never off-loaded
        // to external storage.  We can let it complete in the background.
        // Note that we register no callback for Delete, so AsyncOutput can
        // be freely written to for subsequent operations on this thread.

        if (status == Status.ERROR)
            throw new Exception("Retrieving a key from Faster KV resulted in an error. ");

        bool hasDeleted = (status != Status.NOTFOUND);
        if (hasDeleted)
            Interlocked.Decrement(ref _itemsCount);

        return hasDeleted;
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
        => throw new NotSupportedException();

    private static bool TryGetValueInternal(LocalSession localSession,
                                            in TKey key, 
                                            [MaybeNullWhen(false)] out TValue value)
    {
        var session = localSession.Session;

        var output = new DbOutput();

        Status status = session.Read(ref Unsafe.AsRef(key),
                                     ref output,
                                     localSession.AsyncOutput);

        if (status == Status.PENDING)
        {
            session.CompletePending(wait: true);
            (status, output) = localSession.AsyncOutput;
        }

        if (status == Status.NOTFOUND)
        {
            value = default;
            return false;
        }

        if (status == Status.ERROR)
            throw new Exception("Retrieving a key from Faster KV resulted in an error. ");

        value = output.Value;
        return true;
    }

    public bool TryGetValue(in TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        return FasterDbDictionary<TKey, TValue>.TryGetValueInternal(pooledSession.Target, key, out value);
    }

    bool IDictionary<TKey, TValue>.TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        => TryGetValue(key, out value);

    bool IReadOnlyDictionary<TKey, TValue>.TryGetValue(TKey key, out TValue value)
        => TryGetValue(key, out value);

    protected virtual void DisposeImpl(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _sessionPool.Dispose();
            _storage.Dispose();
            _device.Dispose();
        }
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        DisposeImpl(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The thread-local "session" for invoking FASTER KV operations.
    /// </summary>
    private struct LocalSession : IDisposable
    {
        /// <summary>
        /// The FASTER KV session object.
        /// </summary>
        public readonly ClientSession<TKey, TValue, 
                                      DbInput, DbOutput, 
                                      AsyncOutput, FunctionsImpl> Session;

        /// <summary>
        /// Stores results if the last FASTER KV operation goes pending.
        /// </summary>
        public readonly AsyncOutput AsyncOutput;

        /// <summary>
        /// Instantiates objects for a local session, when it has not
        /// been cached for the current thread.
        /// </summary>
        public LocalSession(FasterDbDictionary<TKey, TValue> parent)
        {
            Session = parent._storage.For(parent._functions).NewSession<FunctionsImpl>();
            AsyncOutput = new AsyncOutput();
        }

        /// <summary>
        /// Disposes the FASTER KV session object when the thread-local
        /// cache is to be cleared.
        /// </summary>
        public void Dispose() => Session.Dispose();
    }

    /// <summary>
    /// Captures the output when an operation in FASTER KV goes "pending".
    /// </summary>
    /// <remarks>
    /// <para>
    /// We are not interested in asynchronous operation here.
    /// When FASTER KV wants to go asynchronous, we have the completion
    /// callbacks write back the results that the calling thread
    /// can access, after it blocks synchronously.  To make that work,
    /// this type must be a reference type which is part of the thread-local
    /// state.
    /// </para>
    /// <para>
    /// This class implements the so-called "context" in FASTER KV.
    /// "Context" is a bad name for the concept, so we call it 
    /// "asynchronous output".
    /// </para>
    /// </remarks>
    private class AsyncOutput
    {
        public Status Status;
        public DbOutput Output;

        /// <summary>
        /// Propagates results from a completion callback made by FASTER KV
        /// so the original caller can read them as if the request completed
        /// synchronously.
        /// </summary>
        public void Copy(Status status, in DbOutput output)
        {
            Status = status;
            Output = output;
        }

        public void Deconstruct(out Status status, out DbOutput output)
        {
            status = Status;
            output = Output;
        }
    }

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
        private readonly FasterDbDictionary<TKey, TValue> _parent;

        public ref ThreadLocalObjectPool<LocalSession, SessionPoolHooks> 
            Root => ref _parent._sessionPool;

        public LocalSession InstantiateObject() => new LocalSession(_parent);

        public SessionPoolHooks(FasterDbDictionary<TKey, TValue> parent)
            => _parent = parent;
    }
}
