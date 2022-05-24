using Microsoft.Extensions.Hosting;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Work;

/// <summary>
/// Incorporates <see cref="WorkerHost" /> as a "long-running" service
/// that is managed by .NET's "generic hosting".
/// </summary>
public sealed class WorkerHostService : IHostedService
{
    private WorkerHost? _workerHost;

    private readonly WorkerHostServiceSettings _settings;
    private readonly WorkerFactory _workerFactory;
    private readonly JobWorkerRpcRegistry? _rpcRegistry;

    /// <summary>
    /// Prepare to start a "long-running" service (as part of the
    /// current application/process) for a worker that executes
    /// jobs on behalf of a job server.
    /// </summary>
    /// <param name="settings">
    /// Configuration to connect to and register into the job server.
    /// </param>
    /// <param name="workerFactory">
    /// Instantiates the implementation of <see cref="IJobSubmission" />
    /// after connecting successfully with the job server.
    /// </param>
    /// <param name="rpcRegistry">
    /// The registry for the RPC connection to the job server.  Typically
    /// it is the server that defines custom functions that
    /// the worker may call remotely, which would not be registered
    /// from the worker's side.  But the worker may still need
    /// to configure serialization in MessagePack of any custom types 
    /// used by those custom functions.  If this argument is null,
    /// it is as if a default-constructed instance is supplied.
    /// </param>
    /// <exception cref="ArgumentException"></exception>
    public WorkerHostService(WorkerHostServiceSettings settings,
                             WorkerFactory workerFactory,
                             JobWorkerRpcRegistry? rpcRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(workerFactory);

        _settings = settings;
        _workerFactory = workerFactory;
        _rpcRegistry = rpcRegistry;

        if (string.IsNullOrEmpty(_settings.ServerUrl))
        {
            throw new ArgumentException("ServerUrl must be specified in the worker's configuration settings. ",
                                        nameof(settings));
        }
    }

    /// <inheritdoc cref="IHostedService.StartAsync"/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        int concurrency = (_settings.Concurrency > 0) ? _settings.Concurrency
                                                      : Environment.ProcessorCount;
        string name = !string.IsNullOrEmpty(_settings.WorkerName) ? _settings.WorkerName
                                                                  : Dns.GetHostName();

        _workerHost = await WorkerHost.ConnectAndStartAsync(
                        _workerFactory,
                        _rpcRegistry,
                        new RegisterWorkerRequestMessage
                        {
                            Concurrency = checked((ushort)concurrency),
                            Name = name
                        },
                        _settings.ServerUrl!,
                        null,
                        cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IHostedService.StopAsync"/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        var workerHost = _workerHost;
        if (workerHost is null)
            return Task.CompletedTask;

        return workerHost.DisposeAsync().AsTask();
    }
}

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
