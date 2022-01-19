using System;
using System.Threading;
using System.Threading.Tasks;
using JobBank.Scheduling;

namespace JobBank.Server
{
    /// <summary>
    /// Asynchronous function that processes the input into an output,
    /// to be executed by a job worker.
    /// </summary>
    public delegate ValueTask<PromiseData>
        PromiseJobFunctionDelegate(object input,
                                   IRunningJob runningJob,
                                   IJobWorker<PromiseJobFunction, PromiseData> worker,
                                   CancellationToken cancellationToken);

    /// <summary>
    /// Encapsulates the function to execute to generate 
    /// the output for a promise.
    /// </summary>
    public readonly struct PromiseJobFunction
    {
        /// <summary>
        /// Input to the processing function.
        /// </summary>
        /// <remarks>
        /// The input can be <see cref="PromiseData" /> but a more specific
        /// or efficient representation can be used.  For example,
        /// when the job runs in the same process then the input can
        /// be direct objects that do not need further de-serialization.
        /// </remarks>
        public object Input { get; }

        /// <summary>
        /// Asynchronous function that processes the input into an output.
        /// </summary>
        public PromiseJobFunctionDelegate Function { get; }

        /// <summary>
        /// Constructor.
        /// </summary>
        public PromiseJobFunction(object input,
                                  PromiseJobFunctionDelegate function)
        {
            Input = input;
            Function = function;
        }

        /// <summary>
        /// Invokes the asynchronous function to process the input.
        /// </summary>
        public ValueTask<PromiseData> 
            InvokeAsync(IRunningJob runningJob, 
                        IJobWorker<PromiseJobFunction, PromiseData> worker,
                        CancellationToken cancellationToken)
            => Function.Invoke(Input, runningJob, worker, cancellationToken);
    }
}
