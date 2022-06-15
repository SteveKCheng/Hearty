using System;
using System.IO;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Common;
using Microsoft.Extensions.Primitives;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Hearty.Client;

/// <summary>
/// Translates payloads that are downloaded from a Hearty server.
/// </summary>
/// <typeparam name="T">
/// The type of object to represent the payload as.
/// </typeparam>
/// <remarks>
/// <para>
/// This structure abstracts out the translation or de-serialization
/// of payloads received from the Hearty server.
/// </para>
/// <para>
/// An application may pre-construct instances of this type,
/// one for each type of output object that it wishes to receive
/// from the Hearty server.
/// </para>
/// </remarks>
public readonly struct PayloadReader<T>
{
    /// <summary>
    /// The IANA media types, with optional quality values, 
    /// to inform the server that this reader will accept.
    /// </summary>
    public StringValues ContentTypes { get; }

    private readonly bool _throwOnException;

    /// <summary>
    /// Whether the ownership of the 
    /// response stream should be transferred to the reader.
    /// </summary>
    /// <remarks>
    /// If false, the stream should be disposed after the
    /// reader function is invoked.  If true, the stream
    /// is assumed to be transferred into the reader function,
    /// which may transfer it back out in its return value.
    /// </remarks>
    internal bool OwnsStream { get; }

    private readonly Func<PromiseId,
                          ParsedContentType, 
                          Stream, 
                          CancellationToken, ValueTask<T>> _streamReader;

    internal static void VerifyContentTypesSyntax(StringValues contentTypes)
    {
        foreach (var contentType in contentTypes)
        {
            if (!new ParsedContentType(contentTypes).IsValid)
                throw new FormatException("The content type to accept for the job output is invalid. ");
        }
    }

    /// <summary>
    /// Encapsulate a function to be invoked by a Hearty client
    /// to translate payloads downloaded from the server.
    /// </summary>
    /// <param name="contentTypes">
    /// The IANA media types to accept for reading and translation.
    /// </param>
    /// <param name="streamReader">
    /// The function that translates or de-serializes the payload
    /// into instances of <typeparamref name="T" />.
    /// </param>
    /// <param name="throwOnException">
    /// When a downloaded payload represents <see cref="ExceptionPayload" />,
    /// and this argument is true, then that exception 
    /// gets thrown, without invoking <paramref name="streamReader" />.
    /// If this argument is false, 
    /// the payload is passed into <paramref name="streamReader" />
    /// without filtering for exceptional payloads.
    /// </param>
    /// <param name="ownsStream">
    /// If false, the response stream should be disposed after 
    /// <paramref name="streamReader"/> is invoked.  If true, 
    /// the stream is assumed to be transferred into that function,
    /// which may transfer it back out in its return value.
    /// </param>
    /// <remarks>
    /// The first argument to <paramref name="streamReader"/> is 
    /// the ID of the promise or job that the payload came from.
    /// The second argument is the actual
    /// media type for the payload received from the server.
    /// The third argument is the byte stream for the downloaded
    /// payload.  It should be assumed that the stream is read-only 
    /// and does not support seeking.
    /// </remarks>
    public PayloadReader(StringValues contentTypes, 
                         Func<PromiseId, ParsedContentType, Stream, CancellationToken, ValueTask<T>> streamReader,
                         bool throwOnException = true,
                         bool ownsStream = false)
    {
        VerifyContentTypesSyntax(contentTypes);
        ContentTypes = contentTypes;
        OwnsStream = ownsStream;
        _streamReader = streamReader ?? throw new ArgumentNullException(nameof(streamReader));
        _throwOnException = throwOnException;
    }

    private static async ValueTask<T> AwaitExceptionPayload(ValueTask<ExceptionPayload?> exceptionTask)
    {
        var exceptionPayload = (await exceptionTask.ConfigureAwait(false))!;
        throw exceptionPayload.ToException();
    }

    internal ValueTask<T> ReadFromStreamAsync(PromiseId promiseId,
                                              ParsedContentType contentType,
                                              Stream stream,
                                              CancellationToken cancellationToken)
    {
        if (_throwOnException)
        {
            var exceptionTask = ExceptionPayload.TryReadAsync(promiseId,
                                                              contentType,
                                                              stream, 
                                                              cancellationToken);
            if (!exceptionTask.IsCompleted)
                return AwaitExceptionPayload(exceptionTask);

            var exceptionPayload = exceptionTask.GetAwaiter().GetResult();
            if (exceptionPayload is not null)
                throw exceptionPayload.ToException();
        }

        return _streamReader.Invoke(promiseId, contentType, stream, cancellationToken);
    }

    internal void AddAcceptHeaders(HttpHeaders httpHeaders, string headerName)
    {
        if (_throwOnException && ContentTypes.Count > 0)
            httpHeaders.TryAddWithoutValidation(headerName, ExceptionPayload.JsonMediaType);

        foreach (var contentType in ContentTypes)
            httpHeaders.TryAddWithoutValidation(headerName, contentType);
    }

    internal void AddAcceptHeaders(HttpHeaders httpHeaders)
        => AddAcceptHeaders(httpHeaders, HeaderNames.Accept);
}
