using FASTER.core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Hearty.Server.FasterKV;

public partial class FasterDbDictionary<TKey, TValue> : IDictionary<TKey, TValue>
{
    private sealed class KeysCollection : ICollection<TKey>
    {
        private readonly FasterDbDictionary<TKey, TValue> _parent;

        public KeysCollection(FasterDbDictionary<TKey, TValue> parent)
        {
            _parent = parent;
        }

        public int Count => _parent.Count;

        bool ICollection<TKey>.IsReadOnly => true;

        void ICollection<TKey>.Add(TKey item) => throw new NotSupportedException();

        void ICollection<TKey>.Clear() => throw new NotSupportedException();

        bool ICollection<TKey>.Contains(TKey item)
            => _parent.ContainsKey(item);

        void ICollection<TKey>.CopyTo(TKey[] array, int arrayIndex)
        {
            using var pooledSession = _parent._sessionPool.GetForCurrentThread();
            using var iterator = pooledSession.Target.Session.Iterate();

            while (arrayIndex < array.Length && iterator.GetNext(out var recordInfo))
                array[arrayIndex++] = iterator.GetKey();
        }

        public IEnumerator<TKey> GetEnumerator()
        {
            using var enumerator = _parent.GetEnumerator();
            while (enumerator.MoveNext())
                yield return enumerator.Current.Key;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        bool ICollection<TKey>.Remove(TKey item) => throw new NotSupportedException();
    }

    private sealed class ValuesCollection : ICollection<TValue>
    {
        private readonly FasterDbDictionary<TKey, TValue> _parent;

        public ValuesCollection(FasterDbDictionary<TKey, TValue> parent)
        {
            _parent = parent;
        }

        public int Count => _parent.Count;

        bool ICollection<TValue>.IsReadOnly => true;

        void ICollection<TValue>.Add(TValue item) => throw new NotSupportedException();

        void ICollection<TValue>.Clear() => throw new NotSupportedException();

        bool ICollection<TValue>.Contains(TValue item)
        {
            using var enumerator = _parent.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (_parent.ValueComparer.Equals(enumerator.Current.Value, item))
                    return true;
            }

            return false;
        }

        void ICollection<TValue>.CopyTo(TValue[] array, int arrayIndex)
        {
            using var pooledSession = _parent._sessionPool.GetForCurrentThread();
            using var iterator = pooledSession.Target.Session.Iterate();

            while (arrayIndex < array.Length && iterator.GetNext(out var recordInfo))
                array[arrayIndex++] = iterator.GetValue();
        }

        public IEnumerator<TValue> GetEnumerator()
        {
            using var enumerator = _parent.GetEnumerator();
            while (enumerator.MoveNext())
                yield return enumerator.Current.Value;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        bool ICollection<TValue>.Remove(TValue item) => throw new NotSupportedException();
    }

    /// <summary>
    /// Manual implementation of <see cref="FasterDbDictionary{TKey, TValue}.GetEnumerator" />
    /// to prevent concurrent use (likely causing FasterKV or the session pooling to crash).
    /// </summary>
    private sealed class Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        public KeyValuePair<TKey, TValue> Current { get; private set; }

        object IEnumerator.Current => Current;

        public Enumerator(FasterDbDictionary<TKey, TValue> parent)
        {
            _pooledSession = parent._sessionPool.GetForCurrentThread();
            _iterator = _pooledSession.Target.Session.Iterate();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isActive, 1) != 0)
                ThrowForReentrancy();

            try
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                _iterator.Dispose();
                _pooledSession.Dispose();
            }
            finally
            {
                _isActive = 0;
            }
        }

        private static void ThrowForReentrancy()
        {
            throw new InvalidOperationException(
                "This enumerator may not be accessed concurrently from multiple threads. ");
        }

        public bool MoveNext()
        {
            if (Interlocked.Exchange(ref _isActive, 1) != 0)
                ThrowForReentrancy();

            try
            {
                bool success = _iterator.GetNext(out _);
                Current = success ? new(_iterator.GetKey(), _iterator.GetValue())
                                  : default!;
                return success;
            }
            finally
            {
                _isActive = 0;
            }
        }

        void IEnumerator.Reset() => throw new NotSupportedException();

        private readonly ThreadLocalObjectPool<LocalSession, SessionPoolHooks>.Use _pooledSession;
        private readonly IFasterScanIterator<TKey, TValue> _iterator;
        private int _isActive;
        private bool _isDisposed;
    }

    /// <summary>
    /// Iterate through the items stored in the database.
    /// </summary>
    /// <returns>
    /// Enumerator for the items.  A different snapshot of the
    /// items may be seen each time this method is called.
    /// </returns>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        using var iterator = pooledSession.Target.Session.Iterate();

        while (arrayIndex < array.Length && iterator.GetNext(out var recordInfo))
            array[arrayIndex++] = new(iterator.GetKey(), iterator.GetValue());
    }

    /// <inheritdoc cref="IDictionary{TKey, TValue}.Keys" />
    public ICollection<TKey> Keys => new KeysCollection(this);

    /// <inheritdoc cref="IDictionary{TKey, TValue}.Values" />
    public ICollection<TValue> Values => new ValuesCollection(this);

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;
}
