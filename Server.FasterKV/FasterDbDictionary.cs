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

    private struct DbInput
    {
        public Func<TKey, TValue>? Factory;
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

        public override void InitialUpdater(ref TKey key, ref DbInput input, ref TValue value, ref DbOutput output)
        {
            var result = input.Factory is not null ? input.Factory(key)
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

    public bool TryAdd(in TKey key, Func<TKey, TValue> factory, out TValue storedValue)
    {
        using var session = _storage.For(_functions).NewSession<FunctionsImpl>();

        var input = new DbInput
        {
            Factory = factory
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
