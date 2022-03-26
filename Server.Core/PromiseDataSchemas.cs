using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;

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
/// <param name="info">
/// Basic information about the payload as reported by 
/// <see cref="PromiseData.GetSerializationInfo" />.
/// </param>
/// <param name="pipeReader">
/// Where the payload for the serialized promise data should be read from.
/// On success, this function should read exactly the number of bytes
/// reported by <see cref="PromiseDataSerializationInfo.PayloadLength" />.
/// </param>
/// <returns>
/// The re-materialized instance of <see cref="PromiseData" />.
/// </returns>
public delegate PromiseData PromiseDataDeserializer(in PromiseDataSerializationInfo info,
                                                    PipeReader pipeReader);


/// <summary>
/// Basic information about an instance of <see cref="PromiseData" /> 
/// to start decoding its serialization.
/// </summary>
public readonly struct PromiseDataSerializationInfo
{
    /// <summary>
    /// The length of the payload, in bytes, that the derived class of
    /// <see cref="PromiseData" /> serializes.
    /// </summary>
    /// <remarks>
    /// This length must be known upfront.  It is populated
    /// into the header for the serialization of the 
    /// containing promise, and may be consulted to pre-allocate
    /// buffers.
    /// </remarks>
    public uint PayloadLength { get; init; }

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
}
