using System;
using System.Net;

namespace Hearty.Work;

/// <summary>
/// Configuration for <see cref="WorkerHostService" />.
/// </summary>
/// <remarks>
/// This type is intended to be read from the JSON configuration 
/// of the worker application/process, through "configuration model binding". 
/// </remarks>
public class WorkerHostServiceSettings
{
    /// <summary>
    /// The URL to the job server's WebSocket endpoint.
    /// </summary>
    /// <remarks>
    /// This URL must have the "ws" or "wss" scheme.
    /// </remarks>
    public string? ServerUrl { get; init; }

    /// <summary>
    /// The name of the worker when registering it to the job server.
    /// </summary>
    /// <remarks>
    /// If null or empty, the name of the worker defaults to the name of the local
    /// computer (or OS-level container), as determined by <see cref="Dns.GetHostName" />.
    /// </remarks>
    public string? WorkerName { get; init; }

    /// <summary>
    /// Degree of concurrency to register the worker as having.
    /// </summary>
    /// <remarks>
    /// This is usually the number of CPUs or hyperthreads, depending on the application.
    /// If zero or negative, <see cref="Environment.ProcessorCount" /> is substituted
    /// when the worker registers with the job server.
    /// </remarks>
    public int Concurrency { get; init; }

    /// <summary>
    /// If true, the application host is requested to stop when the server 
    /// closes its side of the connection gracefully.
    /// </summary>
    /// <remarks>
    /// If false, only <see cref="WorkerHostService" /> stops but 
    /// the containing the application is left alone.
    /// </remarks>
    public bool StopHostWhenServerCloses { get; init; } = true;

    /// <summary>
    /// Number of times to retry connecting to the job server before
    /// giving up.
    /// </summary>
    public int ConnectionRetries { get; init; } = 10;

    /// <summary>
    /// Amount of time to wait, in milliseconds, between connection retries.
    /// </summary>
    public int ConnectionRetryInterval { get; init; } = 5000;
}
