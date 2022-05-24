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
public readonly struct WorkerHostServiceSettings
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
}
