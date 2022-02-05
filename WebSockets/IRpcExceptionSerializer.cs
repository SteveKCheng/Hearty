using System;
using System.Buffers;

namespace JobBank.WebSockets
{
    /// <summary>
    /// Allows customizing how .NET exceptions are serialized by the RPC system.
    /// </summary>
    public interface IRpcExceptionSerializer
    {
        /// <summary>
        /// Translate a .NET exception to an implementation-defined payload
        /// to prepare for transmission.
        /// </summary>
        /// <remarks>
        /// The payload may be the instance of <see cref="Exception" /> itself,
        /// and then <see cref="SerializePayload" /> can do the translation
        /// at the same time as it is serializing.  However, <see cref="SerializePayload" />
        /// is only called when the exception message is the next item
        /// in the queue to send to the remote host, while this method
        /// gets called very soon after the exception is received
        /// by the RPC system on this side, on the same thread it was
        /// thrown, before queuing.  Thus this method is in a better
        /// position to query the exception or augment it with more
        /// contextual information. </remarks>
        /// <param name="exception">The .NET exception to translate. </param>
        /// <returns>
        /// An implementation-defined payload which is passed 
        /// to <see cref="SerializePayload" /> when it is about to be transmitted.
        /// </returns>
        object PreparePayload(Exception exception);

        /// <summary>
        /// Serialize an exception payload into buffers.
        /// </summary>
        /// <param name="writer">Byte buffers to hold the payload to transmit.
        /// </param>
        /// <param name="payload">
        /// The payload object obtained from <see cref="PreparePayload" />.
        /// </param>
        void SerializePayload(IBufferWriter<byte> writer, object payload);

        /// <summary>
        /// De-serialize a payload and materialize a .NET exception out of it.
        /// </summary>
        /// <param name="payload">
        /// The received buffer of bytes written by <see cref="SerializePayload" />
        /// on the remote end.
        /// </param>
        /// <returns>
        /// The payload translated to a .NET exception object.
        /// </returns>
        Exception DeserializeToException(in ReadOnlySequence<byte> payload);
    }
}
