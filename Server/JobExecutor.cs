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
    /// Promise ID that the executor may remember to de-duplicate jobs
    /// with the same inputs.  If <see cref="Job.PromiseId" />
    /// is populated (by a different ID) then the promise represented 
    /// by this ID may be deleted afterwards.
    /// </param>
    /// <returns>
    /// Parsed description of the job to make available to (other) clients.
    /// This function should return as soon as the input has been read and parsed
    /// determine the job to execute.  Reading and parsing inputs
    /// may be part of the asynchronous work of this function, but the
    /// actual work that the job entails should be set as an asynchronous task
    /// in <see cref="Job.Task"/>.
    /// </returns>
    public delegate ValueTask<Job> JobExecutor(JobInput input, PromiseId promiseId);

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
    /// <para>
    /// To clarify on the terminology used in this .NET API, the "promise"
    /// is the shared object that clients can consult to retrieve results,
    /// while the "job" refers to the process and the work to provide
    /// those results to the promise.
    /// </para>
    /// <para>
    /// All settable properties here default to null, or the "default" instance
    /// for value types.
    /// </para>
    /// </remarks>
    public struct Job
    {
        /// <summary>
        /// The ID that clients can look up to retrieve the promise object
        /// for the job.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This property is non-null when the job has been determined to be 
        /// fulfilled by another existing promise, with the ID given here.  
        /// In that case all the other properties in this structure should 
        /// be left at their default values.  
        /// </para>
        /// <para>
        /// This ID must refer to a valid promise that is not the one that had
        /// just been created for the job.
        /// </para>
        /// </remarks>
        public PromiseId? PromiseId { get; }

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

        /// <summary>
        /// The input request data that had been submitted to start this job.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This input is basically what the client has uploaded in the HTTP
        /// POST body to start the job, but there is no requirement that the
        /// input be pristine.  It is registered only so it can be echoed
        /// back to clients that observe the cached promise. 
        /// </para>
        /// <para>
        /// The job executor is allowed to normalize inputs to help with
        /// de-duplicating jobs.  It might even not provide the full inputs, 
        /// which would mean either the job can never be de-duplicated, or 
        /// de-duplication happens in a manner which is not entirely 
        /// reproducible/observable, e.g. by comparing (cryptographic)
        /// digests of the request data.
        /// </para>
        /// </remarks>
        public PromiseOutput? RequestOutput { get; set; }

        /// <summary>
        /// Task that completes when the job executor is done reading 
        /// the request data from the client.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The job executor function is allowed to continue reading the request data 
        /// from the client after it has provided the initial properties for the job
        /// present in this structure.  If so, the Job Bank framework may need to
        /// await this task before it can send a status reply to the client or
        /// close the connection, depending on how the communication protocol works.
        /// </para>
        /// <para>
        /// In particular, this task completing must imply that <see cref="JobInput.PipeReader"/>
        /// has been completely consumed.
        /// </para>
        /// </remarks>
        public ValueTask RequestReadingDone { get; set; }

        /// <summary>
        /// Report that an output object will be provided in the
        /// future for the job.
        /// </summary>
        public Job(ValueTask<PromiseOutput> task)
        {
            PromiseId = null;
            Progress = null;
            RequestOutput = null;
            Task = task;
            RequestReadingDone = ValueTask.CompletedTask;
        }

        /// <summary>
        /// Report the output object that is synchronously available
        /// for the job.
        /// </summary>
        /// <remarks>
        /// Note that as <see cref="PromiseOutput" /> is an asynchronous
        /// interface, calling this constructor does not imply
        /// that the job must be complete.
        /// </remarks>
        public Job(PromiseOutput result)
            : this(ValueTask.FromResult(result))
        {
        }
        
        /// <summary>
        /// Refer to an existing promise for the (eventual) results of
        /// a job.
        /// </summary>
        public Job(PromiseId promiseId)
        {
            PromiseId = promiseId;
            Progress = null;
            RequestOutput = null;
            Task = default;
            RequestReadingDone = ValueTask.CompletedTask;
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
