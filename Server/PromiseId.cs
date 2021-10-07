using System;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace JobBank.Server
{
    /// <summary>
    /// A long-lived identifier for a promise that can refer
    /// to it even if the <see cref="Promise" /> object
    /// has been swapped out of memory.
    /// </summary>
    /// <remarks>
    /// This is a 64-bit integer split into two parts:
    /// the upper 32-bit word is generated for each instance
    /// of the promise service, and the lower 32-bit word
    /// is a sequence number.  The upper 32-bit word lets multiple
    /// service instances synchronize promises with each other 
    /// when recovering from failures.  Using 128-bit GUIDs for
    /// promise IDs would be ultra-safe, but is not done for the
    /// sake of conserving memory.
    /// </remarks>
    public readonly struct PromiseId : IComparable<PromiseId>
                                     , IEquatable<PromiseId>
    {
        /// <summary>
        /// The 64-bit integer representing the promise ID.
        /// </summary>
        private readonly ulong _number;

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
        /// Display the promise ID in the canonical format defined
        /// by the Job Bank framework.
        /// </summary>
        /// <remarks>
        /// The service ID is displayed as fixed-length hexadecimal,
        /// while the sequence number is in decimal without leading zeros.
        /// The two parts are separated by a slash ('/').
        /// </remarks>
        public override string ToString()
        {
            return $"{ServiceId:X8}/{SequenceNumber}";
        }

        /// <summary>
        /// Attempt to parse a promise ID from its components represented as strings.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<char> serviceIdStr, 
                                    ReadOnlySpan<char> sequenceNumberStr,
                                    out PromiseId promiseId)
        {
            if (uint.TryParse(serviceIdStr, NumberStyles.HexNumber, null, out var serviceId))
            {
                if (uint.TryParse(sequenceNumberStr, NumberStyles.None, null, out var sequenceNumber))
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

        public static bool operator ==(PromiseId left, PromiseId right)
            => left.Equals(right);

        public static bool operator !=(PromiseId left, PromiseId right)
            => !left.Equals(right);

        public static bool operator <(PromiseId left, PromiseId right)
            => left.CompareTo(right) < 0;

        public static bool operator <=(PromiseId left, PromiseId right)
            => left.CompareTo(right) >= 0;

        public static bool operator >(PromiseId left, PromiseId right)
            => left.CompareTo(right) > 0;

        public static bool operator >=(PromiseId left, PromiseId right)
            => left.CompareTo(right) >= 0;
    }
}
