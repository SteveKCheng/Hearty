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
/// </remarks>
/// <typeparam name="TKey">
/// The data type for the key in the dictionary.
/// </typeparam>
/// <typeparam name="TValue">
/// The data type for the values in the dictionary.
/// </typeparam>
public partial class FasterDbDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDisposable
    where TKey : unmanaged
    where TValue : struct
{
    private readonly FunctionsImpl _functions = new();
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

    private sealed class FunctionsImpl : FunctionsBase<TKey, TValue, DbInput, DbOutput, Empty>
    {
        public FunctionsImpl() : base(locking: false) { }

        public override void ConcurrentReader(ref TKey key, ref DbInput input, ref TValue value, ref DbOutput output)
            => output.Value = value;

        public override void SingleReader(ref TKey key, ref DbInput input, ref TValue value, ref DbOutput output)
            => output.Value = value;

        public override bool InPlaceUpdater(ref TKey key, ref DbInput input, ref TValue value, ref DbOutput output)
        {
            output.Value = value;
            return true;
        }

        public override bool NeedCopyUpdate(ref TKey key, ref DbInput input, ref TValue oldValue, ref DbOutput output)
            => false;

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
        IDevice device;
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

        _storage = new FasterKV<TKey, TValue>(
                    size: 1L << 20,
                    logSettings: new LogSettings
                    {
                        LogDevice = device
                    },
                    checkpointSettings: null,
                    serializerSettings: null,
                    comparer: comparer,
                    variableLengthStructSettings: varLenSettings);
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
    /// <returns>
    /// Whether the item has been added by the factory function.
    /// </returns>
    public unsafe bool TryAdd<TState>(ref TState state, 
                                      in TKey key, 
                                      DictionaryItemFactory<TState, TKey, TValue> factory)
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

        using var session = _storage.For(_functions).NewSession<FunctionsImpl>();

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
        using var session = _storage.For(_functions).NewSession<FunctionsImpl>();

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
        using var session = _storage.For(_functions).NewSession<FunctionsImpl>();

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
        using var session = _storage.For(_functions).NewSession<FunctionsImpl>();

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
            _storage.Dispose();
        }
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        DisposeImpl(disposing: true);
        GC.SuppressFinalize(this);
    }
}
