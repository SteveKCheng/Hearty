using System;
using MessagePack;

namespace JobBank.Common
{
    /// <summary>
    /// Partial representation of an exception object in a safely serializable form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This data type is modelled after .NET exceptions.  But in so far
    /// as Job Bank can be implemented by non-.NET code, accommodations
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
        public string? Class { get; set; }

        /// <summary>
        /// Basic human-readable error message.
        /// </summary>
        [Key("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Miscellaneous information about the source of the exception.
        /// </summary>
        [Key("source")]
        public string? Source { get; set; }

        /// <summary>
        /// Trace of the call stack from where the exception originated.
        /// </summary>
        [Key("trace")]
        public string? Trace { get; set; }

        /// <summary>
        /// Create an instance based on the data in a .NET exception.
        /// </summary>
        public static ExceptionPayload CreateFromException(Exception exception)
        {
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
    }
}
