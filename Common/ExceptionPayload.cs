﻿using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace Hearty.Common;

/// <summary>
/// Partial representation of an exception object in a safely serializable form.
/// </summary>
/// <remarks>
/// <para>
/// This data type is modelled after .NET exceptions.  But in so far
/// as Hearty can be implemented by non-.NET code, accommodations
/// are made for other languages and run-time environments.
/// </para>
/// <para>
/// This data type helps make environments that allow unbounded classes
/// of run-time errors, i.e. "exceptions", 
/// be interoperable over a remote connection or to other programming
/// languages.  The full exception information generally 
/// cannot be captured since most such environments allow unbounded
/// run-time behavior of their exception classes/types also.  But
/// enough declarative detail should be captured about the error
/// to aid in diagnosis and debugging.  
/// </para>
/// </remarks>
[MessagePackObject]
//[JsonSerializable]
public class ExceptionPayload
{
    /// <summary>
    /// The programming language or run-time system that originated
    /// the exception.
    /// </summary>
    /// <remarks>
    /// This property can be consulted to apply a specific interpretation
    /// of the other properties for some programming or run-time system.
    /// </remarks>

    [Key("language")]
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// The value of the <see cref="Language" /> property when the
    /// exception originates from .NET code.
    /// </summary>
    public const string DotNetLanguage = "dotnet";

    /// <summary>
    /// Whether this exception represents a co-operative cancellation 
    /// of an operation.
    /// </summary>
    [Key("cancelling")]
    [JsonPropertyName("cancelling")]
    public bool Cancelling { get; set; }

    /// <summary>
    /// The name of the sub-class or type of exception object.
    /// </summary>
    /// <remarks>
    /// Some de-serializers may elect to re-materialize the 
    /// exception object with its exact sub-class.
    /// Of course, such re-materialization should always be filtered by
    /// a whitelist, to prevent remote-code execution vulnerabilities.
    /// </remarks>
    [Key("class")]
    [JsonPropertyName("class")]
    public string? Class { get; set; }

    /// <summary>
    /// Basic human-readable error message.
    /// </summary>
    [Key("description")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Miscellaneous information about the source of the exception.
    /// </summary>
    [Key("source")]
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Trace of the call stack from where the exception originated.
    /// </summary>
    [Key("trace")]
    [JsonPropertyName("trace")]
    public string? Trace { get; set; }

    /// <summary>
    /// Create an instance based on the data in a .NET exception.
    /// </summary>
    public static ExceptionPayload CreateFromException(Exception exception)
    {
        // Unwrap an exception that was produced by ToException()
        if (exception is RemoteWorkException wrapped)
            return wrapped.Payload;
        else if (exception is RemoteWorkCancelledException wrapped2)
            return wrapped2.Payload;

        return new ExceptionPayload
        {
            Language = DotNetLanguage,
            Class = exception.GetType().FullName,
            Cancelling = exception is OperationCanceledException,
            Description = exception.Message,
            Source = exception.Source,
            Trace = exception.StackTrace,
        };
    }

    /// <summary>
    /// Construct a .NET exception object from this (serialized) payload.
    /// </summary>
    public Exception ToException()
    {
        if (Cancelling)
            return new RemoteWorkCancelledException(this);

        return new RemoteWorkException(this);
    }

    /// <summary>
    /// Load an instance of <see cref="ExceptionPayload" /> from a stream,
    /// if its reported content type matches.
    /// </summary>
    /// <param name="promiseId">
    /// The ID of the promise that originated the payload.
    /// </param>
    /// <param name="contentType">
    /// The IANA media type that the payload has been labelled with.
    /// </param>
    /// <param name="stream">
    /// The stream containing the payload.  It only needs to be readable,
    /// and not necessarily seekable.
    /// </param>
    /// <param name="cancellationToken">
    /// Can be triggered to cancel the reading from the stream.
    /// </param>
    /// <returns>
    /// Asynchronous task that completes with null if <paramref name="contentType" />
    /// does not match <see cref="JsonMediaType" />.  Otherwise, the task
    /// completes with the result of de-serialization, 
    /// assuming that is successful.
    /// </returns>
    public static ValueTask<ExceptionPayload?> TryReadAsync(PromiseId promiseId,
                                                            ParsedContentType contentType, 
                                                            Stream stream,
                                                            CancellationToken cancellationToken)
    {
        if (!contentType.IsSubsetOf(new ParsedContentType(JsonMediaType)))
            return ValueTask.FromResult<ExceptionPayload?>(null);

        return JsonSerializer.DeserializeAsync<ExceptionPayload>(stream, options: null, cancellationToken)!;
    }

    /// <summary>
    /// IANA media type for the JSON serialization of <see cref="ExceptionPayload" />.
    /// </summary>
    public static readonly string JsonMediaType = "application/vnd.hearty.exception+json";
}
