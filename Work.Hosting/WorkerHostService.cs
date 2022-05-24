using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Hearty.Carp;

namespace Hearty.Work;

/// <summary>
/// Incorporates <see cref="WorkerHost" /> as a "long-running" service
/// that is managed by .NET's "generic hosting".
/// </summary>
public sealed class WorkerHostService : IHostedService
{

    private readonly WorkerHostServiceSettings _settings;
    private readonly WorkerFactory _workerFactory;
    private readonly ILogger _logger;
    private readonly JobWorkerRpcRegistry? _rpcRegistry;

    private readonly object _lockObj = new();
    private Task<WorkerHost>? _workerHostTask;
    private bool _hasStarted;
    private CancellationTokenSource? _cancellationSource;

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
    /// <param name="logger">
    /// Logs when connections occur.
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
                             ILogger<WorkerHostService> logger,
                             JobWorkerRpcRegistry? rpcRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(workerFactory);

        _settings = settings;
        _workerFactory = workerFactory;
        _rpcRegistry = rpcRegistry;
        _logger = logger;

        if (string.IsNullOrEmpty(_settings.ServerUrl))
        {
            throw new ArgumentException("ServerUrl must be specified in the worker's configuration settings. ",
                                        nameof(settings));
        }
    }

    private Task<WorkerHost> ConnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting the worker host...");

        int concurrency = (_settings.Concurrency > 0) ? _settings.Concurrency
                                                      : Environment.ProcessorCount;
        string name = !string.IsNullOrEmpty(_settings.WorkerName) ? _settings.WorkerName
                                                                  : Dns.GetHostName();

        var policy = Policy.Handle<Exception>()
                           .WaitAndRetryAsync(retryCount: 10, _ => TimeSpan.FromSeconds(5));

        int attempt = 0;
        return policy.ExecuteAsync(async (cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ensure that writes to the instance variables of this class 
            // from StartAsync occur before WorkerHost_OnClose can possibly
            // execute.
            await Task.Yield();

            _logger.LogInformation("Attempting to connect: attempt {attempt}", ++attempt);

            var workerHost = await WorkerHost.ConnectAndStartAsync(
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

            _logger.LogInformation("Successfully connected to job server. ");

            workerHost.OnClose += WorkerHost_OnClose;

            // Could happen if there is a race with the connection failing
            // and the event handler being attached.
            // FIXME: have workerHost be able to report the exception correctly
            if (workerHost.HasClosed)
                throw new Exception("Connection to job server failed right after it had been connected to. ");

            return workerHost;
        }, cancellationToken);
    }


    /// <inheritdoc cref="IHostedService.StartAsync"/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_lockObj)
        {
            if (_hasStarted)
                return Task.CompletedTask;

            _workerHostTask = ConnectAsync(cancellationSource.Token);
            _cancellationSource = cancellationSource;
            _hasStarted = true;
        }

        return Task.CompletedTask;
    }

    private void WorkerHost_OnClose(object? sender, RpcConnectionCloseEventArgs e)
    {
        var exception = e.Exception;
        if (exception is null)
            return;

        _logger.LogError(exception, "Connection to job server failed. ");

        var cancellationSource = new CancellationTokenSource();

        lock (_lockObj)
        {
            if (!_hasStarted)
                return;

            _workerHostTask = ConnectAsync(cancellationSource.Token);
            _cancellationSource = cancellationSource;
        }
    }

    /// <inheritdoc cref="IHostedService.StopAsync"/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource cancellationSource;
        Task<WorkerHost> workerHostTask;

        lock (_lockObj)
        {
            if (!_hasStarted)
                return;

            cancellationSource = _cancellationSource!;
            workerHostTask = _workerHostTask!;

            _hasStarted = false;
            _cancellationSource = null;
            _workerHostTask = null;
        }

        _logger.LogInformation("Shutting down the running worker host... ");
        cancellationSource.Cancel();

        WorkerHost workerHost;
        try
        {
            workerHost = await workerHostTask.WaitAsync(cancellationToken)
                                             .ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        await workerHost.DisposeAsync().ConfigureAwait(false);
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
