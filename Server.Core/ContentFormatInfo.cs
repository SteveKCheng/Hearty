using Hearty.Common;
using System;

namespace Hearty.Server;

/// <summary>
/// Describes the format of data available from a server
/// for content negotiation.
/// </summary>
/// <remarks>
/// This structure describes the outputs available from a promise.
/// It adopts a simplified version of content negotation from HTTP,
/// geared towards programmatically generated data.
/// </remarks>
public readonly partial struct ContentFormatInfo : IComparable<ContentFormatInfo>
                                                 , IEquatable<ContentFormatInfo>
{
    /// <summary>
    /// The type of the content provided, identified by an IANA "media type" label.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If <see cref="IsContainer" /> is true, this label refers to the
    /// wrapper structure of items where there are multiple.  If 
    /// <see cref="IsContainer" /> is false, this label refers to
    /// the individual items.
    /// </para>
    /// <para>
    /// This member is typed as <see cref="ParsedContentType" />, so
    /// that <see cref="ContentFormatInfo" /> can provide a cached
    /// value for content negotation, thus avoiding parsing the
    /// media type string over and over again.
    /// </para>
    /// </remarks>
    public ParsedContentType MediaType { get; }

    /// <summary>
    /// Whether this format describes the wrapper structure for
    /// a payload with multiple items.
    /// </summary>
    public bool IsContainer => (_flags & 0x1) != 0;

    /// <summary>
    /// The preference for this format on the part of the server
    /// or provider.
    /// </summary>
    public ContentPreference Preference
        => (ContentPreference)((_flags >> 1) & 0x3);

    /// <summary>
    /// Whether the content can be downloaded partially.
    /// </summary>
    public ContentSeekability CanSeek
        => (ContentSeekability)((_flags >> 3) & 0x3);

    /// <summary>
    /// Holds the properties of the format other than its string
    /// <see cref="MediaType" /> in compressed form.
    /// </summary>
    private readonly uint _flags;

    /// <summary>
    /// Constructs an instance with the specified information. 
    /// </summary>
    /// <param name="mediaType">The IANA "media type". 
    /// <see cref="ParsedContentType.IsValid" /> should be true,
    /// even though that fact is not checked by this constructor
    /// for performance reasons.  An invalid media type will not
    /// match during content negoation.
    /// </param>
    /// <param name="preference">Indicates preference for this format. </param>
    /// <param name="canSeek">Indicates if content with this format
    /// can be partially downloaded. </param>
    /// <param name="isContainer">Whether <paramref name="mediaType"/> refers
    /// to the wrapper structure, or the items within. </param>
    public ContentFormatInfo(ParsedContentType mediaType,
                             ContentPreference preference,
                             ContentSeekability canSeek = ContentSeekability.None,
                             bool isContainer = false)
    {
        MediaType = mediaType;
        _flags = (isContainer ? 1u : 0) |
                 ((uint)preference & 0x3) << 1 |
                 ((uint)canSeek & 0x3) << 3;
    }

    /// <summary>
    /// Compares instances, in order of preference.
    /// </summary>
    /// <param name="other">The item to compare against. </param>
    /// <returns>
    /// The return value is less than 0 if 
    /// this item should occur before the other in preference order,
    /// greater than 0 if it should occur later;
    /// and equal to zero if the items are the same.
    /// </returns>
    public int CompareTo(ContentFormatInfo other)
    {
        int c;

        c = ((int)other.Preference).CompareTo((int)this.Preference);
        if (c != 0) return c;

        c = ((int)other.CanSeek).CompareTo((int)this.Preference);
        if (c != 0) return c;

        c = (other.IsContainer ? 1 : 0).CompareTo(this.IsContainer ? 1 : 0);
        if (c != 0) return c;

        return this.MediaType.Input.AsSpan().SequenceCompareTo(other.MediaType.Input.AsSpan());
    }

    /// <summary>
    /// Compare for structural equality of all properties.
    /// </summary>
    /// <param name="other">The other instance to compare 
    /// this item against. </param>
    /// <returns>
    /// True if equal, false if not.
    /// </returns>
    public bool Equals(ContentFormatInfo other)
    {
        return string.Equals(this.MediaType, other.MediaType) &&
               this._flags == other._flags;
    }

    /// <inheritdoc cref="object.Equals(object?)" />
    public override bool Equals(object? obj)
        => obj is ContentFormatInfo other && Equals(other);

    /// <inheritdoc cref="GetHashCode" />
    public override int GetHashCode()
        => HashCode.Combine(MediaType, _flags);

    public static bool operator ==(ContentFormatInfo left, ContentFormatInfo right)
        => left.Equals(right);

    public static bool operator !=(ContentFormatInfo left, ContentFormatInfo right)
        => !(left == right);

    public static bool operator <(ContentFormatInfo left, ContentFormatInfo right)
        => left.CompareTo(right) < 0;

    public static bool operator <=(ContentFormatInfo left, ContentFormatInfo right)
        => left.CompareTo(right) <= 0;

    public static bool operator >(ContentFormatInfo left, ContentFormatInfo right)
        => left.CompareTo(right) > 0;

    public static bool operator >=(ContentFormatInfo left, ContentFormatInfo right)
        => left.CompareTo(right) >= 0;
}

/// <summary>
/// Indicates the preference for an output format, 
/// for content negotiation.
/// </summary>
public enum ContentPreference : byte
{
    /// <summary>
    /// A choice that is made available for compatibility
    /// but is recommended against.
    /// </summary>
    /// <remarks>
    /// The implementation can materialize such a format,
    /// but may require significant resources to do so,
    /// or may lose heavy amounts of information.
    /// </remarks>
    Bad = 0,

    /// <summary>
    /// A choice that is not preferred.
    /// </summary>
    /// <remarks>
    /// The implementation can materialize such a format,
    /// possibly with some loss of information.
    /// </remarks>
    Fair = 1,

    /// <summary>
    /// A choice which may not be the best but still recommended.
    /// </summary>
    /// <remarks>
    /// The implementation can speedily materialize such a format
    /// with minimal loss of information.
    /// </remarks>
    Good = 2,

    /// <summary>
    /// A best choice.
    /// </summary>
    /// <remarks>
    /// A "best" format is typically one that the implementation
    /// stores natively, or one that can be speedily 
    /// generated from the native/canonical/internal format 
    /// while not losing information.
    /// </remarks>
    Best = 3
}

/// <summary>
/// Indicates if output can be downloaded partially
/// from a server, without having to start at the beginning.
/// </summary>
/// <remarks>
/// Output in certain formats that are generated on demand
/// by the server may not be seekable; the generation
/// process must start at the beginning.
/// </remarks>
public enum ContentSeekability : byte
{
    /// <summary>
    /// The content is not seekable at all.
    /// </summary>
    None = 0,

    /// <summary>
    /// A specific range of bytes in the content may be requested
    /// by the client.
    /// </summary>
    Bytes = 1,

    /// <summary>
    /// A specific range of items may be requested by the client.
    /// </summary>
    /// <remarks>
    /// This choice only applies to content that consists of
    /// multiple items.  The items may have variable length
    /// in bytes, so the content is not seekable in bytes,
    /// but the items may be directly indexed (in the server's
    /// storage of the items).
    /// </remarks>
    Items = 2,

    /// <summary>
    /// Both ranges of bytes or items may be requested by the client.
    /// </summary>
    /// <remarks>
    /// This choice only applies to content that consists of
    /// multiple items.
    /// </remarks>
    Both = 3
}
