﻿using System;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Threading;

using Hearty.Common;

namespace Hearty.Server.WebApi
{
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
    /// The data provided by a client when it requests a promise. 
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the promise entails spawning a job then this data may serve
    /// as the input for the job as well.  But there is not necessarily
    /// a one-to-one relationship between promises and jobs (items
    /// to execute in the job scheduling system).  Promises may be
    /// cached so that the request for the same promise returns
    /// the existing promise and not re-spawn a job to compute its
    /// outputs.
    /// </para>
    /// </remarks>
    public readonly struct PromiseRequest
    {
        /// <summary>
        /// The instance of <see cref="PromiseStorage" />
        /// being exposed to the client and that is 
        /// the target of this request.
        /// </summary>
        public PromiseStorage Storage { get; init; }

        /// <summary>
        /// The IANA media type that the payload, as a byte stream,
        /// has been labelled with.
        /// </summary>
        /// <remarks>
        /// This member corresponds with the "Content-Type" field
        /// sent by the client in the HTTP POST request.
        /// </remarks>
        public string? ContentType { get; init; }

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
        public long? ContentLength { get; init; }

        /// <summary>
        /// Incrementally reads the byte stream of the payload.
        /// </summary>
        /// <remarks>
        /// This pipe must be completed by the promise producer 
        /// before it returns.  Thus, promises cannot be created
        /// from on partially received data; the protocols used
        /// for the Web APIs from Hearty impose this restriction.
        /// </remarks>
        public PipeReader PipeReader { get; init; }

        /// <summary>
        /// Cancellation token for the request itself.
        /// </summary>
        public CancellationToken CancellationToken { get; init; }

        /// <summary>
        /// Whether the main job should be launched in the background
        /// without requiring its promise to be awaited.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If true, the job should be launched without
        /// <see cref="CancellationToken" />, but registered
        /// with the queue allowing the job to be remotely cancelled.
        /// See <see cref="JobsManager" />.  The client is expected
        /// to retrieve the results of the promise later using
        /// the returned <see cref="PromiseId" /> of the job.
        /// </para>
        /// <para>
        /// If false, <see cref="CancellationToken" /> should be
        /// observed for cancellation of the job, when requested by 
        /// the user, or on some otherwise non-recoverable error.
        /// The promise for the job is expected to be awaited.
        /// </para>
        /// </remarks>
        public bool FireAndForget { get; init; }

        /// <summary>
        /// Routing key for the endpoint that received this request
        /// from the remote client.
        /// </summary>
        public string? RouteKey { get; init; }

        /// <summary>
        /// The job queue selected by the client for any jobs that 
        /// need to be pushed to satisfy this request.
        /// </summary>
        /// <remarks>
        /// The receiver of this information may assume the client 
        /// has been authorized for this queue.  Authorization
        /// is expected to be the responsibility of the overall 
        /// server framework, e.g. ASP.NET Core.
        /// </remarks>
        public JobQueueKey JobQueueKey { get; init; }

        /// <summary>
        /// "Principal" object describing the owner of the queue
        /// or promise if this information should be recorded.
        /// </summary>
        public ClaimsPrincipal? OwnerPrincipal { get; init; }
    }
}
