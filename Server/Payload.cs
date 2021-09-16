using System;
using System.Linq;

namespace JobBank.Server
{
    /// <summary>
    /// Encapsulates a payload, stored in memory, of arbitrary content (media) type
    /// that is uploaded or downloaded.
    /// </summary>
    public readonly struct Payload
    {
        /// <summary>
        /// Content (media) type describing the data format of <see cref="Body"/>.
        /// </summary>
        public string ContentType { get; }

        /// <summary>
        /// The sequence of bytes forming the user-defined data.
        /// </summary>
        public Memory<byte> Body { get; }

        public Payload(string contentType, Memory<byte> body)
        {
            ContentType = contentType;
            Body = body;
        }

        public bool Equals(in Payload other)
            => ContentType == other.ContentType && Body.Span.SequenceEqual(other.Body.Span);
    }
}
