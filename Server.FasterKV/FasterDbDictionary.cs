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
/// backing storage even though FASTER KV does.
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
/// </remarks>
/// <typeparam name="TKey">
/// The data type for the key in the dictionary.
/// </typeparam>
/// <typeparam name="TValue">
/// The data type for the values in the dictionary.
/// </typeparam>
public partial class FasterDbDictionary<TKey, TValue> 
    : IDictionary<TKey, TValue>, IDisposable
    where TKey : unmanaged
    where TValue : struct
{
    private readonly FunctionsImpl _functions = new();
    private readonly IDevice _device;
    private readonly FasterKV<TKey, TValue> _storage;
    private bool _isDisposed;

    // FIXME need to restore this count when reading a file
    private long _itemsCount;

    private unsafe struct DbInput
    {
        /// <summary>
        /// Instance of <see cref="DictionaryItemFactory{TState, TKey, TValue}" />
        /// that been type-erased.
        /// </summary>
        public object? Factory;

        /// <summary>
        /// Pinned pointer to the state object for the factory.
        /// </summary>
        public void* State;

        /// <summary>
        /// Address of an instantiation of <see cref="InvokerImpl{TState}" />.
        /// </summary>
        public delegate*<ref DbInput, in TKey, TValue> Invoker;

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
    private sealed class FunctionsImpl : FunctionsBase<TKey, TValue, DbInput, DbOutput, Empty>
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
            var result = input.Invoker != null ? input.Invoker(ref input, key)
                                               : input.Value;
            value = result;
            output.Value = result;
        }
    }

    public FasterDbDictionary(in FasterDbFileOptions fileOptions,
                              IFasterEqualityComparer<TKey>? comparer = null,
                              VariableLengthStructSettings<TKey, TValue>? varLenSettings = null)
    {
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
    /// Invokes the user's factory function, for FASTER KV's RMW operation,
    /// after (unsafe) casting from the type-erased pointer of the state back 
    /// to a type-correct reference.
    /// </summary>
    /// <typeparam name="TState">
    /// The state object of the user's factory function.
    /// </typeparam>
    /// <param name="input">
    /// Internal input to FASTER KV for adding a new item
    /// along with the user's factory function after type erasure.
    /// </param>
    /// <param name="key">
    /// The key for the new item.
    /// </param>
    /// <returns>
    /// The value for the item produced by the user's factory function.
    /// </returns>
    private static unsafe TValue InvokerImpl<TState>(ref DbInput input, in TKey key)
    {
        return Unsafe.As<DictionaryItemFactory<TState, TKey, TValue>>(input.Factory!)
                     .Invoke(ref Unsafe.AsRef<TState>(input.State), key);
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
        // C# does not allow pinning an arbitrary TState, so we
        // have to make a copy on the stack to ensure the garbage
        // collector can never move it, then call Unsafe.AsPointer below.
        var stateCopy = state;

        var input = new DbInput
        {
            Factory = factory ?? throw new ArgumentNullException(nameof(factory)),
            State = Unsafe.AsPointer(ref stateCopy),
            Invoker = &InvokerImpl<TState>
        };

        using var pooledSession = _sessionPool.GetForCurrentThread();
        var session = pooledSession.Target;

        var output = new DbOutput();

        try
        {
            Status status;
            while ((status = session.RMW(ref Unsafe.AsRef(key), ref input, ref output)) == Status.PENDING)
                session.CompletePending();

            if (status == Status.ERROR)
                throw new Exception("Retrieving a key from Faster KV resulted in an error. ");

            bool hasAdded = (status == Status.NOTFOUND);
            if (hasAdded)
                Interlocked.Increment(ref _itemsCount);

            storedValue = output.Value;
            return hasAdded;
        }
        finally
        {
            // Propagate changes on stateCopy back to caller
            state = stateCopy;
        }
    }

    public bool TryAdd(in TKey key, in TValue value)
        => TryAdd(key, value, out _);

    public bool TryAdd(in TKey key, in TValue value, out TValue storedValue)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        var session = pooledSession.Target;

        var input = new DbInput
        {
            Value = value
        };

        var output = new DbOutput();

        Status status;
        while ((status = session.RMW(ref Unsafe.AsRef(key), ref input, ref output)) == Status.PENDING)
            session.CompletePending();

        if (status == Status.ERROR)
            throw new Exception("Retrieving a key from Faster KV resulted in an error. ");

        storedValue = output.Value;

        bool hasAdded = (status == Status.NOTFOUND);
        if (hasAdded)
            Interlocked.Increment(ref _itemsCount);

        return hasAdded;
    }

    public TValue this[TKey key] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    /// <inheritdoc cref="IDictionary{TKey, TValue}.Keys" />
    public ICollection<TKey> Keys => new KeysCollection(this);

    /// <inheritdoc cref="IDictionary{TKey, TValue}.Values" />
    public ICollection<TValue> Values => new ValuesCollection(this);

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

    public void Clear()
    {
        throw new NotImplementedException();
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc cref="IDictionary{TKey, TValue}.ContainsKey(TKey)" />
    public bool ContainsKey(TKey key)
    {
        return TryGetValue(key, out _);
    }

    /// <inheritdoc cref="IDictionary{TKey, TValue}.Remove" />
    public bool Remove(TKey key)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        var session = pooledSession.Target;

        Status status;
        while ((status = session.Delete(ref key)) == Status.PENDING)
            session.CompletePending();

        if (status == Status.ERROR)
            throw new Exception("Retrieving a key from Faster KV resulted in an error. ");

        bool hasDeleted = (status == Status.OK);
        if (hasDeleted)
            Interlocked.Decrement(ref _itemsCount);

        return hasDeleted;
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
    {
        throw new NotImplementedException();
    }

    public bool TryGetValue(in TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        var session = pooledSession.Target;

        Status status;
        value = default;

        var output = new DbOutput();

        while ((status = session.Read(ref Unsafe.AsRef(key), ref output)) == Status.PENDING)
            session.CompletePending();

        if (status == Status.NOTFOUND)
            return false;

        if (status == Status.ERROR)
            throw new Exception("Retrieving a key from Faster KV resulted in an error. ");

        value = output.Value;
        return true;
    }

    bool IDictionary<TKey, TValue>.TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
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

    private ThreadLocalObjectPool<
                ClientSession<TKey, TValue, DbInput, DbOutput, Empty, FunctionsImpl>,
                SessionPoolHooks> _sessionPool;

    private struct SessionPoolHooks : IThreadLocalObjectPoolHooks<
        ClientSession<TKey, TValue, DbInput, DbOutput, Empty, FunctionsImpl>, SessionPoolHooks>
    {
        private readonly FasterDbDictionary<TKey, TValue> _parent;

        public ref ThreadLocalObjectPool<ClientSession<TKey, TValue, DbInput, DbOutput, Empty, FunctionsImpl>, 
                                         SessionPoolHooks> 
            Root => ref _parent._sessionPool;

        public ClientSession<TKey, TValue, DbInput, DbOutput, Empty, FunctionsImpl> 
            InstantiateObject()
        {
            return _parent._storage.For(_parent._functions).NewSession<FunctionsImpl>();
        }

        public SessionPoolHooks(FasterDbDictionary<TKey, TValue> parent)
            => _parent = parent;
    }
}
