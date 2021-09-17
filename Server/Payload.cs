using System;
using System.Linq;

namespace JobBank.Server
{
    /// <summary>
    /// Encapsulates a payload, stored in memory, of arbitrary content (media) type
    /// that is uploaded or downloaded.
    /// </summary>
    public readonly struct Payload : IEquatable<Payload>
    {
        /// <summary>
        /// Content (media) type describing the data format of <see cref="Body"/>,
        /// specified in the same way as HTTP and MIME.
        /// </summary>
        public string ContentType { get; }

        /// <summary>
        /// The sequence of bytes forming the user-defined data.
        /// </summary>
        public Memory<byte> Body { get; }

        /// <summary>
        /// Label a sequence of bytes with its content type.
        /// </summary>
        /// <param name="contentType"><see cref="ContentType"/>. </param>
        /// <param name="body"><see cref="Body"/>. </param>
        public Payload(string contentType, Memory<byte> body)
        {
            ContentType = contentType;
            Body = body;
        }

        /// <summary>
        /// A payload equals another if they have the same content type
        /// (compared as strings) and the contents are byte-wise equal.
        /// </summary>
        /// <param name="other">The payload to compare this payload against. </param>
        public bool Equals(Payload other)
            => ContentType == other.ContentType && Body.Span.SequenceEqual(other.Body.Span);

        /// <inheritdoc />
        public override bool Equals(object? obj)
            => obj is Payload payload && Equals(payload);

        /// <inheritdoc />
        public override int GetHashCode()
            => ContentType.GetHashCode();   // FIXME should hash first few bytes of Body
    }
}
