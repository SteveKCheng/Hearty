using System;

namespace JobBank.Server
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
        /// Alternative representation of the input to the 
        /// processing function.
        /// </summary>
        /// <remarks>
        /// The job request should be adequately described by 
        /// <see cref="PromiseData" />, but this property can 
        /// contain a more specific or efficient representation
        /// of that data.  For example, when the job runs in the 
        /// same process then the input can be direct objects that 
        /// do not need further de-serialization.
        /// </remarks>
        public object? Hint { get; init; }

        /// <summary>
        /// A condensed string representation, expected to be
        /// unique within a context, of the promise or the work.
        /// </summary>
        public string? Path { get; init; }

        /// <summary>
        /// The promise object that originated this work.
        /// </summary>
        public Promise? Promise { get; init; }

        /// <summary>
        /// A generic and serializable representation of the 
        /// inputs to the job.
        /// </summary>
        /// <remarks>
        /// This object may not necessarily be 
        /// <see cref="Promise.RequestOutput" />, if the promise
        /// is designed to not faithfully preserve its inputs
        /// (for storage efficiency).
        /// </remarks>
        public PromiseData Data { get; }

        /// <summary>
        /// The initial estimate of the amount of time the job
        /// would take, in milliseconds.
        /// </summary>
        public int InitialWait { get; init; }

        /// <summary>
        /// Encapsulate certain input data to submit as work 
        /// for a promise.
        /// </summary>
        /// <param name="data">Sets <see cref="Data" />.
        /// </param>
        /// <remarks>
        /// Only <see cref="Data" /> is a required property;
        /// all other properties of the work are optional.
        /// </remarks>
        public PromisedWork(PromiseData data)
        {
            Data = data;
            Route = default;
            Hint = default;
            Path = default;
            Promise = default;
            InitialWait = default;
        }
    }
}
