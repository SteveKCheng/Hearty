using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Common;

namespace Hearty.Client
{
    /// <summary>
    /// Delegate invoked by the Hearty client to de-serialize payloads
    /// downloaded from the server into the user's desired .NET objects.
    /// </summary>
    /// <typeparam name="T">
    /// The type of object the payload will be de-serialized to.
    /// </typeparam>
    /// <param name="contentType">
    /// The IANA media type reported by the Hearty server for the payload.
    /// </param>
    /// <param name="stream">
    /// The stream that the payload should be read from.  It should
    /// be assumed that this stream is read-only and does not support
    /// seeking.
    /// </param>
    /// <param name="cancellationToken">
    /// This token is passed through from the method in the Hearty client 
    /// being invoked and may be triggered to cancel the entire
    /// downloading operation.
    /// </param>
    /// <returns>
    /// Asynchronous task that completes with the object de-serialized 
    /// from the payload, or an exception if de-serialization fails.
    /// </returns>
    public delegate ValueTask<T> PayloadReader<T>(ParsedContentType contentType,
                                                  Stream stream,
                                                  CancellationToken cancellationToken);
}
