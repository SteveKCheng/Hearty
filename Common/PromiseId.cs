using System;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using static System.FormattableString;
using System.Text;
using System.Text.Json.Serialization;

using Hearty.Common;

namespace Hearty;


/// <summary>
/// A long-lived identifier for a promise.
/// </summary>
/// <remarks>
/// <para>
/// This type is a 64-bit integer split into two parts:
/// the upper 32-bit word is generated for each instance
/// of the promise service, and the lower 32-bit word
/// is a sequence number.  The upper 32-bit word lets multiple
/// service instances synchronize promises with each other 
/// when recovering from failures.  Using 128-bit GUIDs for
/// promise IDs would be ultra-safe, but is not done for the
/// sake of conserving memory.
/// </para>
/// <para>
/// Clients refer to promises on the server side through this type.
/// On the server side, this type can unambiguously 
/// identify the promise object if it has been swapped out of memory.
/// </para>
/// </remarks>
[JsonConverter(typeof(PromiseIdJsonConverter))]
public readonly struct PromiseId : IComparable<PromiseId>
                                 , IEquatable<PromiseId>
                                 , IConvertible
#if NET6_0_OR_GREATER
                                 , ISpanFormattable
#endif
{
    /// <summary>
    /// The 64-bit integer representing the promise ID.
    /// </summary>
    private readonly ulong _number;

    /// <summary>
    /// The maximum number of characters taken by the
    /// canonical textual representation of a promise ID.
    /// </summary>
    public static readonly int MaxChars = 9 + 10;

    /// <summary>
    /// Construct the promise ID from its constituent parts.
    /// </summary>
    public PromiseId(uint serviceId, uint sequenceNumber)
    {
        _number = (((ulong)serviceId) << 32) | sequenceNumber;
    }

    /// <summary>
    /// Construct the promise ID from the raw 64-bit integer.
    /// </summary>
    public PromiseId(ulong number)
    {
        _number = number;
    }

    /// <summary>
    /// The ID assigned to the service instance that
    /// created the promise.
    /// </summary>
    public uint ServiceId => (uint)(_number >> 32);

    /// <summary>
    /// The sequence number for the promise generated
    /// by the service instance.
    /// </summary>
    public uint SequenceNumber => (uint)(_number & 0xFFFFFFFF);

    /// <summary>
    /// The raw 64-bit integer representation of the promise ID.
    /// </summary>
    public ulong RawInteger => _number;

    /// <summary>
    /// Display the promise ID in the canonical format defined
    /// by the Hearty framework.
    /// </summary>
    /// <remarks>
    /// The service ID is displayed as fixed-length hexadecimal,
    /// while the sequence number is in decimal without leading zeros.
    /// The two parts are separated by a slash ('/').
    /// </remarks>
    public override string ToString()
    {
        return Invariant($"{ServiceId:X8}/{SequenceNumber}");
    }

    /// <summary>
    /// Attempt to parse a promise ID from its string representation
    /// as returned by <see cref="ToString()" />.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> input,
                                out PromiseId promiseId)
    {
        promiseId = default;
        if (input.Length < 10 || input[8] != '/')
        {
            promiseId = default;
            return false;
        }

        return TryParse(input[0..8], input[9..], out promiseId);
    }

    /// <summary>
    /// Attempt to parse a promise ID from its components represented as strings.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> serviceIdStr, 
                                ReadOnlySpan<char> sequenceNumberStr,
                                out PromiseId promiseId)
    {
        if (uint.TryParse(serviceIdStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var serviceId))
        {
            if (uint.TryParse(sequenceNumberStr, NumberStyles.None, CultureInfo.InvariantCulture, out var sequenceNumber))
            {
                promiseId = new PromiseId(serviceId, sequenceNumber);
                return true;
            }
        }

        promiseId = default;
        return false;
    }

    /// <summary>
    /// Compares promise IDs as integers.
    /// </summary>
    public int CompareTo(PromiseId other)
        => _number.CompareTo(other._number);

    /// <inheritdoc cref="IEquatable{T}.Equals"/>
    public bool Equals(PromiseId other)
        => _number == other._number;

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj)
        => obj is PromiseId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _number.GetHashCode();

    /// <summary>
    /// Returns whether this ID is equal to another.
    /// </summary>
    public static bool operator ==(PromiseId left, PromiseId right)
        => left.Equals(right);

    /// <summary>
    /// Returns whether this ID is not equal to another.
    /// </summary>
    public static bool operator !=(PromiseId left, PromiseId right)
        => !left.Equals(right);

    /// <summary>
    /// Returns whether one ID occurs before another in this type's
    /// canonical sorting order.
    /// </summary>
    public static bool operator <(PromiseId left, PromiseId right)
        => left.CompareTo(right) < 0;

    /// <summary>
    /// Returns whether one ID occurs before, or is equal
    /// to, another in this type's canonical sorting order.
    /// </summary>
    public static bool operator <=(PromiseId left, PromiseId right)
        => left.CompareTo(right) >= 0;

    /// <summary>
    /// Returns whether one ID occurs after another in this type's
    /// canonical sorting order.
    /// </summary>
    public static bool operator >(PromiseId left, PromiseId right)
        => left.CompareTo(right) > 0;

    /// <summary>
    /// Returns whether one ID occurs after, or is equal
    /// to, another in this type's canonical sorting order.
    /// </summary>
    public static bool operator >=(PromiseId left, PromiseId right)
        => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
#if NET6_0_OR_GREATER
        return destination.TryWrite(CultureInfo.InvariantCulture, $"{ServiceId:X8}/{SequenceNumber}", out charsWritten);
#else
        if (destination.Length < MaxChars)
        {
            charsWritten = 0;
            return false;
        }

        var s = ToString();
        s.AsSpan().CopyTo(destination);
        charsWritten = s.Length;
        return true;
#endif
    }

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider)
        => ToString();

    /// <summary>
    /// Format the Promise ID as an ASCII string.
    /// </summary>
    /// <param name="destination">
    /// Buffer to hold the ASCII string.  Must be sized
    /// for at least <see cref="MaxChars" />.
    /// </param>
    /// <returns>
    /// The number of bytes written to <paramref name="destination" />.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The buffer <paramref name="destination" /> is too small.
    /// </exception>
    public int FormatAscii(Span<byte> destination)
    {
        Span<char> charBuffer = stackalloc char[MaxChars];
        TryFormat(charBuffer, out int charsWritten, null, CultureInfo.InvariantCulture);

        if (destination.Length < charsWritten)
        {
            throw new ArgumentException("Destination buffer is insufficiently sized. ",
                                        nameof(destination));
        }

        Encoding.ASCII.GetBytes(charBuffer, destination);
        return charsWritten;
    }

    /// <summary>
    /// Format the Promise ID as an ASCII string.
    /// </summary>
    /// <param name="destination">
    /// Buffer to hold the ASCII string.  Must be sized
    /// for at least <see cref="MaxChars" />.
    /// </param>
    /// <returns>
    /// The number of bytes written to <paramref name="destination" />.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The buffer <paramref name="destination" /> is too small.
    /// </exception>
    public int FormatAscii(Memory<byte> destination)
        => FormatAscii(destination.Span);

    #region IConvertible

    TypeCode IConvertible.GetTypeCode() => TypeCode.UInt64;

    bool IConvertible.ToBoolean(IFormatProvider? provider) 
        => Convert.ToBoolean(RawInteger, provider);

    byte IConvertible.ToByte(IFormatProvider? provider)
        => Convert.ToByte(RawInteger, provider);

    char IConvertible.ToChar(IFormatProvider? provider)
        => Convert.ToChar(RawInteger, provider);

    DateTime IConvertible.ToDateTime(IFormatProvider? provider)
        => Convert.ToDateTime(RawInteger, provider);

    decimal IConvertible.ToDecimal(IFormatProvider? provider)
        => Convert.ToDecimal(RawInteger, provider);

    double IConvertible.ToDouble(IFormatProvider? provider)
        => Convert.ToDouble(RawInteger, provider);

    short IConvertible.ToInt16(IFormatProvider? provider)
        => Convert.ToInt16(RawInteger, provider);

    int IConvertible.ToInt32(IFormatProvider? provider)
        => Convert.ToInt32(RawInteger, provider);

    long IConvertible.ToInt64(IFormatProvider? provider)
        => (long)RawInteger;

    sbyte IConvertible.ToSByte(IFormatProvider? provider)
        => Convert.ToSByte(RawInteger, provider);

    float IConvertible.ToSingle(IFormatProvider? provider)
        => Convert.ToSingle(RawInteger, provider);

    string IConvertible.ToString(IFormatProvider? provider)
        => ToString();

    object IConvertible.ToType(Type conversionType, IFormatProvider? provider)
        => ((IConvertible)RawInteger).ToType(conversionType, provider);

    ushort IConvertible.ToUInt16(IFormatProvider? provider)
        => Convert.ToUInt16(RawInteger, provider);

    uint IConvertible.ToUInt32(IFormatProvider? provider)
        => Convert.ToUInt32(RawInteger, provider);

    ulong IConvertible.ToUInt64(IFormatProvider? provider)
        => RawInteger;

    #endregion
}
