using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Hearty.Server;

/// <summary>
/// A mapping of schema codes for <see cref="PromiseData" /> to
/// their de-serializers.
/// </summary>
/// <remarks>
/// This class is a registry of schema codes to their de-serializers.
/// It is a dedicated type so that it can be easily put into a dependency
/// injection framework, and it enforces that the mapping cannot
/// (concurrently) change.
/// </remarks>
public sealed class PromiseDataSchemas : IReadOnlyDictionary<ushort, PromiseDataDeserializer>
{
    private readonly ImmutableDictionary<ushort, PromiseDataDeserializer> _entries;

    /// <summary>
    /// Construct the mapping of schema codes in one shot.
    /// </summary>
    /// <param name="entries">
    /// Listing of schema codes with their associated de-serializers.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="entries"/> contains duplicate keys.
    /// </exception>
    public PromiseDataSchemas(IEnumerable<KeyValuePair<ushort, PromiseDataDeserializer>> entries)
        => _entries = entries.ToImmutableDictionary();

    /// <summary>
    /// Construct from a mapping of schema codes that has been incrementally built.
    /// </summary>
    /// <param name="builder">
    /// Mapping of schema codes to their associated de-serializers.
    /// </param>
    public PromiseDataSchemas(ImmutableDictionary<ushort, PromiseDataDeserializer>.Builder builder)
    {
        if (!object.ReferenceEquals(builder.KeyComparer, EqualityComparer<ushort>.Default))
            throw new ArgumentException("Comparer of keys for PromiseDataSchemas must be the default one, but is not. ");

        _entries = builder.ToImmutable();
    }

    /// <summary>
    /// Get the de-serializer corresponding to a schema code.
    /// </summary>
    /// <param name="key">The schema code. </param>
    /// <returns>The de-serializer function. </returns>
    public PromiseDataDeserializer this[ushort key] => _entries[key];

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Keys" />
    public IEnumerable<ushort> Keys => _entries.Keys;

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Values" />
    public IEnumerable<PromiseDataDeserializer> Values => _entries.Values;

    /// <inheritdoc cref="IReadOnlyCollection{T}.Count" />
    public int Count => _entries.Count;

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.ContainsKey" />
    public bool ContainsKey(ushort key) => _entries.ContainsKey(key);

    /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />
    public IEnumerator<KeyValuePair<ushort, PromiseDataDeserializer>> GetEnumerator()
        => _entries.GetEnumerator();

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.TryGetValue" />
    public bool TryGetValue(ushort key, [MaybeNullWhen(false)] out PromiseDataDeserializer value)
        => _entries.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Materializes promise data from a sequence of bytes, i.e. de-serializes.
/// </summary>
/// <param name="buffer">
/// Where the payload for the serialized promise data should be read from.
/// </param>
/// <remarks>
/// <para>
/// De-serialization needs to be optimized for data that comes 
/// from a database.  Many database APIs provide only "blob" values as one
/// contiguous buffer.  In the case of memory-mapped databases (e.g. LMDB),
/// the buffer might even point to the database storage area, which this
/// function could directly load from.  Of course, the database storage area
/// is likely subject to compaction, so the materialized <see cref="PromiseData" />
/// must still copy the data once, but this function is designed so data
/// is not copied twice.  
/// </para>
/// <para>
/// In principle, promise data could be received over the network.  Do note
/// that this serialization feature would only used for multiple job servers
/// to synchronize the promises they store, for resilience and scalability.
/// This feature is not for (remote) clients of job servers.
/// </para>
/// <para>
/// For such applications, the serialized payload is expected to be stored
/// in some (local) database anyway.  Thus there is no occasion to call
/// this function to read directly from a network source.  Accordingly,
/// this function is not asynchronous, and the length of the serialized data
/// must be known upfront.
/// </para>
/// </remarks>
/// <returns>
/// The re-materialized instance of <see cref="PromiseData" />.
/// </returns>
public delegate PromiseData PromiseDataDeserializer(ReadOnlySpan<byte> buffer);

/// <summary>
/// Writes out the serialized (promise) data into a buffer, synchronously.
/// </summary>
/// <param name="info">
/// The serialization state and information obtained from 
/// <see cref="PromiseData.TryPrepareSerialization" />.
/// </param>
/// <param name="buffer">
/// The buffer to write the serialized data into. 
/// It is sized to exactly <see cref="PromiseDataSerializationInfo.PayloadLength" />.
/// </param>
public delegate void PromiseDataSerializer(in PromiseDataSerializationInfo info,
                                           Span<byte> buffer);

/// <summary>
/// Information prepared for serializing an instance of <see cref="PromiseData" />.
/// </summary>
public readonly struct PromiseDataSerializationInfo
{
    /// <summary>
    /// The length of the payload, in bytes, that the derived class of
    /// <see cref="PromiseData" /> serializes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This length must be known upfront.  It is populated
    /// into the header for the serialization of the 
    /// containing promise, and may be consulted to pre-allocate
    /// buffers.
    /// </para>
    /// <para>
    /// The length is limited to the capacity of <see cref="Int32" />,
    /// since <see cref="Span{T}" /> has the same limitation.
    /// The serialization system is likely to have smaller limits,
    /// e.g. databases are unlikely to support blobs as large as 2GB.
    /// </para>
    /// </remarks>
    public int PayloadLength { get; init; }

    /// <summary>
    /// An internal code indicating how to de-serialize the payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These codes are registered into <see cref="PromiseDataSchemas" />
    /// so that the correct derived class of <see cref="PromiseData" /> 
    /// to instantiate can be looked up.
    /// </para>
    /// <para>
    /// These codes are private to the job server, and are not exposed
    /// to clients.  Also, any instance of job server is going to
    /// use only a handful of derived classes of <see cref="PromiseData" />.  
    /// So, a 16-bit integer suffices to encompass all reasonable codes,
    /// and reduces size and complexity (especially compared to 
    /// arbitrary strings).
    /// </para>
    /// </remarks>
    public ushort SchemaCode { get; init; }

    /// <summary>
    /// Arbitrary state object consulted by <see cref="Serializer" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This reference may point to the instance of <see cref="PromiseData" />.
    /// </para>
    /// <para>
    /// However, some implementations of <see cref="PromiseData" /> may not
    /// know the length of the serialized data without producing it.
    /// For such implementations, <see cref="PromiseData.TryPrepareSerialization" />
    /// must serialize into temporary re-sizable buffers.  Then this property may be
    /// set to those temporary buffers so that <see cref="Serializer" />
    /// can just copy the results over to the final (pinned) buffer.
    /// </para>
    /// </remarks>
    public object? State { get; init; }

    /// <summary>
    /// Function to write out the serialization after a buffer of
    /// size <see cref="PayloadLength" /> has been allocated for it.
    /// </summary>
    public PromiseDataSerializer Serializer { get; init; }
    
    /// <summary>
    /// If <see cref="State" /> is not null, this function is called
    /// after serialization happens or has been abandoned.
    /// </summary>
    /// <remarks>
    /// This function may be used to return temporary buffers
    /// to an array pool, etc.
    /// </remarks>
    public Action<object>? StateDisposal { get; init; }
}
