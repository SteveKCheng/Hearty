using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Called by the Job Bank system when a job is posted by a client.
    /// </summary>
    /// <param name="input">
    /// Inputs that describe the job.
    /// </param>
    /// <param name="promiseStorage">
    /// Can be used to create a promise for the job
    /// or look up an existing promise.
    /// </param>
    /// <returns>
    /// ID of the either an existing promise or a newly created promise
    /// that will provide the result (asynchronously).
    /// </returns>
    public delegate ValueTask<PromiseId> 
        JobExecutor(JobInput input, 
                    PromiseStorage promiseStorage);

    /// <summary>
    /// Progress updates on a background job for a promise.
    /// </summary>
    public struct JobProgress
    {
        /// <summary>
        /// The number of discrete items or stages that have completed.
        /// </summary>
        public int Numerator { get; set; }

        /// <summary>
        /// The total number of discrete items or stages.
        /// </summary>
        /// <remarks>
        /// No discrete count will be considered if this member is not positive.
        /// </remarks>
        public int Denominator { get; set; }

        /// <summary>
        /// A fractional measurement of the degree of completion.
        /// </summary>
        /// <remarks>
        /// This number should be from zero to one, or NaN if no
        /// fraction is being reported.
        /// </remarks>
        public double Fraction { get; set; }

        /// <summary>
        /// A brief message describing what the background job is doing.
        /// </summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// The inputs to the job provided by the client.
    /// </summary>
    public readonly struct JobInput
    {
        /// <summary>
        /// The IANA media type that the payload, as a byte stream,
        /// has been labelled with.
        /// </summary>
        /// <remarks>
        /// This member corresponds with the "Content-Type" field
        /// sent by the client in the HTTP POST request.
        /// </remarks>
        public string? ContentType { get; }

        /// <summary>
        /// Supposed size of the payload, in bytes.
        /// </summary>
        /// <remarks>
        /// This member corresponds with the "Content Length" field
        /// sent by the client in the HTTP POST request.
        /// This number should be treated as advisory, as some protocols
        /// may allow the client to "lie" about the length.
        /// The final size of the payload can only be determined by
        /// consuming <see cref="PipeReader"/> (fully).
        /// </remarks>
        public long? ContentLength { get; }

        /// <summary>
        /// Incrementally reads the byte stream of the payload.
        /// </summary>
        /// <remarks>
        /// This pipe must be completed by the job executor before
        /// <see cref="Job.RequestReadingDone"/> completes.
        /// </remarks>
        public PipeReader PipeReader { get; }

        /// <summary>
        /// For cancelling the job when requested by users, or on some
        /// otherwise non-recoverable error.
        /// </summary>
        public CancellationToken CancellationToken { get; }

        internal JobInput(string? contentType, 
                          long? contentLength, 
                          PipeReader pipeReader, 
                          CancellationToken cancellationToken)
        {
            ContentType = contentType;
            ContentLength = contentLength;
            PipeReader = pipeReader;
            CancellationToken = cancellationToken;
        }
    }
}
