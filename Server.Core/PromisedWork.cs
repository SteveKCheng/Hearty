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
        public string? Route { get; }

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
        public object? Hint { get; }

        /// <summary>
        /// A generic and serializable representation of the 
        /// inputs to the job.
        /// </summary>
        public PromiseData Data { get; }

        /// <summary>
        /// The initial estimate of the amount of time the job
        /// would take, in milliseconds.
        /// </summary>
        public int InitialWait { get; }

        /// <summary>
        /// Constructor.
        /// </summary>
        public PromisedWork(PromiseData data, 
                            int initialWait,
                            string? route = null,
                            object? hint = null)
        {
            Route = route;
            InitialWait = initialWait;
            Hint = hint;
            Data = data;
        }
    }
}
