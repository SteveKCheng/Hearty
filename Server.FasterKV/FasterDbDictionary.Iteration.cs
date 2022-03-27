using System;
using System.Collections;
using System.Collections.Generic;

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
            using var iterator = pooledSession.Target.Iterate();

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
            using var iterator = pooledSession.Target.Iterate();

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
    /// Iterate through the items stored in the database.
    /// </summary>
    /// <returns>
    /// Enumerator for the items.  A different snapshot of the
    /// items may be seen each time this method is called.
    /// </returns>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        using var iterator = pooledSession.Target.Iterate();

        while (true)
        {
            KeyValuePair<TKey, TValue> result;

            lock (iterator)
            {
                if (!iterator.GetNext(out var recordInfo))
                    break;

                result = new(iterator.GetKey(), iterator.GetValue());
            }

            yield return result;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        using var pooledSession = _sessionPool.GetForCurrentThread();
        using var iterator = pooledSession.Target.Iterate();

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
