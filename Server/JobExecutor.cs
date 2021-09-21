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
    /// <param name="promise">
    /// Promise object that this function may cache to de-duplicate jobs
    /// with the same inputs.
    /// </param>
    /// <returns>
    /// Parsed description of the job to make available to (other) clients.
    /// This function should return as soon as the input has been read and parsed
    /// determine the job to execute.  Reading and parsing inputs
    /// may be part of the asynchronous work of this function, but the
    /// actual work that the job entails should be set as an asynchronous task
    /// in <see cref="Job.Task"/>.
    /// </returns>
    public delegate ValueTask<Job> JobExecutor(JobInput input, Promise promise);

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
    /// Details about the job made available by <see cref="JobExecutor"/>.
    /// </summary>
    /// <remarks>
    /// To clarify on the terminology used in this .NET API, the "promise"
    /// is the shared object that clients can consult to retrieve results,
    /// while the "job" refers to the process and the work to provide
    /// those results to the promise.
    /// </remarks>
    public struct Job
    {
        /// <summary>
        /// The ID that clients can look up to retrieve the promise object
        /// for the job.
        /// </summary>
        public string? PromiseId { get; set; }

        /// <summary>
        /// Provides continuing status updates on the background job.
        /// </summary>
        public IProgress<JobProgress>? Progress { get; set; }

        /// <summary>
        /// A task that returns the results of the job to post into
        /// the Promise object.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The work that the job entails may be queued or may continue in the
        /// background, so an asynchronous task can be set here.  
        /// </para>
        /// <para>
        /// Background work should be run in a task scheduler or a thread pool,
        /// and the task set here should await that work's completion.
        /// </para>
        /// <para>
        /// As the job's description should be reported before any background 
        /// work starts, this asynchronous task is set separately from
        /// the asynchronous task that <see cref="JobExecutor"/> returns.
        /// </para>
        /// </remarks>
        public ValueTask<PromiseOutput> Task { get; }

        public Job(ValueTask<PromiseOutput> task)
        {
            PromiseId = null;
            Progress = null;
            Task = task;
        }

        public Job(PromiseOutput result)
            : this(ValueTask.FromResult(result))
        {
        }
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
        public string ContentType { get; }

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
        /// This byte stream must be consumed completely before
        /// <see cref="JobExecutor"/> returns, as the Job Bank implementation
        /// or underlying protocol may not support keeping open the connection
        /// or pipe receiving the job inputs.  Also, another client 
        /// cannot reliably share a promise initiated by the first, if the
        /// input is only partially determined.
        /// </remarks>
        public PipeReader PipeReader { get; }

        /// <summary>
        /// For cancelling the job when requested by users, or on some
        /// otherwise non-recoverable error.
        /// </summary>
        public CancellationToken CancellationToken { get; }

        internal JobInput(string contentType, 
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
