using System;
using System.Buffers;
using System.Threading.Tasks;

namespace Hearty.Server
{
    /// <summary>
    /// Describes the work to generate the output for a promise.
    /// </summary>
    /// <remarks>
    /// The term "work" is used for this inert data type 
    /// to distinguish it from the many types in this framework
    /// that have the word "job" in their names, which control
    /// job scheduling and queuing.
    /// </remarks>
    public readonly struct PromisedWork
    {
        /// <summary>
        /// A string that may be used to select a sub-function 
        /// to apply to the data.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This property is essentially a command string.  It may
        /// also be a named "route" that <see cref="PromiseData" />
        /// had originated from, e.g. corresponding to an endpoint
        /// on a Web server.  
        /// </para>
        /// <para>
        /// The interpretation of this string is up to the implementation.  
        /// As jobs may need to be serializable and run on remote hosts,
        /// sub-functions of the data cannot be represented by .NET
        /// delegates here.
        /// </para>
        /// </remarks>
        public string? Route { get; init; }

        /// <summary>
        /// A representation of the input (request) 
        /// to the processing function.
        /// </summary>
        /// <remarks>
        /// The job request should be adequately described by 
        /// <see cref="PromiseData" />, but this property can 
        /// contain a more specific or efficient representation
        /// of that data.  For example, when the job runs in the 
        /// same process then the input can be direct objects that 
        /// do not need further de-serialization.
        /// </remarks>
        public object Data { get; }

        /// <summary>
        /// Provides the serialized form of the input to send
        /// to a remote host.
        /// </summary>
        /// <para>
        /// The argument passed in is <see cref="Data" />.
        /// </para>
        /// <para>
        /// This function is asynchronous to allow for <see cref="SimplePayload" />
        /// to represent some kind of reference which can be asynchronously
        /// resolved.  Typical implementations will be synchronous.
        /// </para>
        public Func<object, ValueTask<SimplePayload>> InputSerializer { get; }

        /// <summary>
        /// Transforms the output from the job reported from a remote
        /// worker host into <see cref="PromiseData" />.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The first argument is <see cref="Data" />.
        /// The second argument is the output from the job
        /// received from the remote host.
        /// </para>
        /// <para>
        /// The default is <see cref="Payload.JobOutputDeserializer" />.
        /// </para>
        /// <para>
        /// This function is asynchronous to allow for <see cref="SimplePayload" />
        /// to represent some kind of reference which can be asynchronously
        /// resolved.  Typical implementations will be synchronous.
        /// </para>
        /// </remarks>
        public Func<object, SimplePayload, ValueTask<PromiseData>>
            OutputDeserializer { get; init; }

        /// <summary>
        /// A condensed string representation, expected to be
        /// unique within a context, of the promise or the work.
        /// </summary>
        public string? Path { get; init; }

        /// <summary>
        /// The promise object that originated this work.
        /// </summary>
        /// <remarks>
        /// This property may be used for logging.  It is also
        /// useful to implement <see cref="PromiseRetriever" />
        /// while avoiding allocating objects unnecessarily.
        /// </remarks>
        public Promise? Promise { get; init; }

        /// <summary>
        /// The initial estimate of the amount of time the job
        /// would take, in milliseconds.
        /// </summary>
        public int InitialWait { get; init; }

        /// <summary>
        /// Encapsulate promise input data to submit as work 
        /// for a promise.
        /// </summary>
        /// <param name="data">The object set as <see cref="Data" />, which will
        /// be serialized via <see cref="PromiseData.GetPayloadAsync" />
        /// when the job executes remotely.
        /// </param>
        /// <remarks>
        /// Only <see cref="Data" /> is a required property;
        /// all other properties of the work are optional.
        /// </remarks>
        public PromisedWork(PromiseData data)
            : this(data, Payload.JobInputSerializer)
        {
        }

        /// <summary>
        /// Encapsulate certain input data to submit as work 
        /// for a promise.
        /// </summary>
        /// <param name="data">The object set as <see cref="Data" />, which will
        /// be serialized via <paramref name="inputSerializer" />.
        /// </param>
        /// <param name="inputSerializer">
        /// Serializes the input data when the job executes remotely.
        /// </param>
        /// <remarks>
        /// Only <see cref="Data" /> is a required property;
        /// all other properties of the work are optional.
        /// </remarks>
        public PromisedWork(object data, Func<object, ValueTask<SimplePayload>> inputSerializer)
        {
            Data = data;
            Route = default;
            Path = default;
            Promise = default;
            InitialWait = default;
            InputSerializer = inputSerializer;
            OutputDeserializer = Payload.JobOutputDeserializer;
        }

        /// <summary>
        /// Make a copy of this structure with the
        /// <see cref="Promise" /> property replaced.
        /// </summary>
        /// <param name="promise">
        /// New value for the <see cref="Promise" /> property.
        /// </param>
        public PromisedWork ReplacePromise(Promise? promise)
            => new(this.Data, this.InputSerializer)
            {
                Route = this.Route,
                Path = this.Path,
                Promise = promise,
                InitialWait = this.InitialWait,
                OutputDeserializer = this.OutputDeserializer
            };
    }

    /// <summary>
    /// Simple representation of an in-memory bytes payload with a labelled type.
    /// </summary>
    /// <remarks>
    /// This structure is semantically the equivalent of the 
    /// <see cref="Payload" /> class but does not inherit from 
    /// <see cref="PromiseData" />.  In the context of executing
    /// jobs the <see cref="PromiseData" /> abstraction is not
    /// necessary.
    /// </remarks>
    /// <param name="ContentType">
    /// The IANA media type to label the payload as.
    /// </param>
    /// <param name="Body">
    /// The payload as a sequence of bytes.
    /// </param>
    public record struct SimplePayload(string ContentType, ReadOnlySequence<byte> Body);
}
