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
/// <remarks>
/// <para>
/// The worker will try restarting itself if the connection
/// to the job server fails.
/// </para>
/// <para>
/// Since the protocol between the worker and the job server is
/// stateful, reliable recovery from connection failures is only possible
/// by restarting <see cref="WorkerHost" /> entirely.
/// However, implementations of <see cref="IJobSubmission" />
/// may attempt to checkpoint current computation state, either locally
/// or on the job server itself (through the same RPC connection),
/// so that when the job server re-submits the same jobs, workers can
/// pick from where the jobs left off before the connection failure.
/// </para>
/// <para>
/// Retries are implemented with the help of the Polly library.
/// </para>
/// </remarks>
public sealed class WorkerHostService : IHostedService
{
    private readonly WorkerHostServiceSettings _settings;
    private readonly WorkerFactory _workerFactory;
    private readonly ILogger _logger;
    private IHostApplicationLifetime _appLifetime;
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
    /// <param name="appLifetime">
    /// Used to request the host to quit if the job server closes down
    /// the connection gracefully.
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
                             IHostApplicationLifetime appLifetime,
                             JobWorkerRpcRegistry? rpcRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(workerFactory);

        _settings = settings;
        _workerFactory = workerFactory;
        _rpcRegistry = rpcRegistry;
        _logger = logger;
        _appLifetime = appLifetime;

        if (string.IsNullOrEmpty(_settings.ServerUrl))
        {
            throw new ArgumentException("ServerUrl must be specified in the worker's configuration settings. ",
                                        nameof(settings));
        }
    }

    /// <summary>
    /// The exception from the last attempt at connecting to the job server if it failed.
    /// </summary>
    /// <remarks>
    /// This property evaluates to null if no connection has been attempted, 
    /// if the connection is successful, or if a (re-)connection is ongoing.
    /// </remarks>
    public Exception? ConnectionFailure
    {
        get
        {
            var workerHostTask = _workerHostTask;
            if (_workerHostTask is not null && _workerHostTask.IsCompleted)
                return _workerHostTask.Exception?.InnerException;
            else
                return null;
        }
    }

    /// <summary>
    /// True if <see cref="StartAsync(CancellationToken)" /> has been called,
    /// even if the connection fails (afterwards).
    /// </summary>
    public bool HasStarted => _hasStarted;

    private async Task<WorkerHost> ConnectAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();

        _logger.LogInformation("Starting the worker host...");

        int concurrency = (_settings.Concurrency > 0) ? _settings.Concurrency
                                                      : Environment.ProcessorCount;
        string name = !string.IsNullOrEmpty(_settings.WorkerName) ? _settings.WorkerName
                                                                  : Dns.GetHostName();

        var policy = Policy.Handle<Exception>()
                           .WaitAndRetryAsync(retryCount: _settings.ConnectionRetries, 
                                              _ => TimeSpan.FromMilliseconds(_settings.ConnectionRetryInterval));

        int attempt = 0;

        try
        {
            var workerHost = await policy.ExecuteAsync(async (cancellationToken) =>
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
            }, cancellationToken).ConfigureAwait(false);

            return workerHost;
        }
        catch
        {
            RequestHostToStop();
            throw;
        }
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

    private void RequestHostToStop()
    {
        if (_settings.StopHostWhenServerCloses)
            _appLifetime.StopApplication();
    }

    private void WorkerHost_OnClose(object? sender, RpcConnectionCloseEventArgs e)
    {
        var exception = e.Exception;
        if (exception is null)
        {
            RequestHostToStop();
            return;
        }

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
            await workerHost.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
