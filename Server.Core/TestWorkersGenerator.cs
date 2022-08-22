using Hearty.Carp;
using Hearty.Scheduling;
using Hearty.Utilities;
using Hearty.Work;
using Microsoft.Extensions.Logging;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Instantiates <see cref="WorkerHost" /> in-process 
/// to receive job requests over an internal WebSocket connection.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorkerHost" /> is normally meant to be
/// run on each remote worker host.  This class constructs
/// them in the current process, for testing.
/// </para>
/// <para>
/// Compared to <see cref="LocalWorkerAdaptor" />, this class
/// is able to test the WebSocket connection, the serialization
/// of requests and replies, and allows the implementation
/// of <see cref="IJobSubmission" /> to talk back to the server
/// using the same WebSocket connection.  
/// </para>
/// <para>
/// The WebSocket connection between the worker and the "server"
/// is entirely internal, so that the "server" does not need
/// to implement any network-accessible HTTP server 
/// (e.g. with the ASP.NET Core framework) for this class to work.  
/// This feature is important for unit testing.  But on the other
/// hand, it means any WebSocket endpoint implemented by a HTTP
/// server may have to be separately tested.
/// </para>
/// </remarks>
public class TestWorkersGenerator
{
    /// <summary>
    /// Running count of all the workers created so far,
    /// to generate unique names for them.
    /// </summary>
    private uint _countCreatedWorkers;

    private readonly ILogger _logger;
    private readonly WorkerDistribution<PromisedWork, PromiseData> _workerDistribution;
    private readonly WorkerFactory _implFactory;
    private readonly JobServerRpcRegistry? _serverRpcRegistry;
    private readonly JobWorkerRpcRegistry? _workerRpcRegistry;

    /// <summary>
    /// Prepare to generate local workers.
    /// </summary>
    /// <param name="logger">
    /// Logs calls to generate local workers.
    /// </param>
    /// <param name="workerDistribution">
    /// Local workers will be registered here to receive work to process.
    /// </param>
    /// <param name="workerFactory">
    /// Instantiates the implementation of <see cref="IJobSubmission" />
    /// for the local worker.
    /// </param>
    /// <param name="serverRpcRegistry">
    /// The registry for the job-server side of RPC connections, which
    /// can be customized for the worker to make callbacks to the
    /// server side.  If this argument is null, it is as if a 
    /// default-constructed instance is supplied.
    /// </param>
    /// <param name="workerRpcRegistry">
    /// The registry for the worker side of RPC connections, which
    /// can be customized for the worker to make callbacks to the
    /// server side.  If this argument is null, it is as if a 
    /// default-constructed instance is supplied.
    /// </param>
    public TestWorkersGenerator(ILogger<TestWorkersGenerator> logger,
                                WorkerDistribution<PromisedWork, PromiseData> workerDistribution,
                                WorkerFactory workerFactory,
                                JobServerRpcRegistry? serverRpcRegistry = null,
                                JobWorkerRpcRegistry? workerRpcRegistry = null)
    {
        _logger = logger;
        _workerDistribution = workerDistribution;
        _implFactory = workerFactory;
        _serverRpcRegistry = serverRpcRegistry;
        _workerRpcRegistry = workerRpcRegistry;
    }

    private (WebSocket ClientWebSocket, WebSocket ServerWebSocket) CreateWebSocketPair()
    {
        var (clientStream, serverStream) = DuplexStream.CreatePair();

        var keepAliveInterval = TimeSpan.FromSeconds(60);

        // Layer on the WebSocket protocol on top of the bi-directional streams.
        var serverWebSocket = WebSocket.CreateFromStream(
                                serverStream,
                                isServer: true,
                                subProtocol: null,
                                keepAliveInterval: keepAliveInterval);
        var clientWebSocket = WebSocket.CreateFromStream(
                                clientStream,
                                isServer: false,
                                subProtocol: null,
                                keepAliveInterval: keepAliveInterval);

        return (clientWebSocket, serverWebSocket);
    }

    /// <summary>
    /// Create fake worker hosts connecting to a WebSocket endpoint
    /// for job distribution from a Hearty server.
    /// </summary>
    public async Task GenerateWorkersAsync(int count, int concurrency)
    {
        if (count <= 0)
            return;

        _logger.LogInformation("{count} fake worker(s) will connect", count);

        uint oldCount = Interlocked.Add(ref _countCreatedWorkers, (uint)count) - (uint)count;

        for (int i = 0; i < count; ++i)
        {
            var name = $"remote-worker-{++oldCount}";

            var settings = new RegisterWorkerRequestMessage
            {
                Name = name,
                Concurrency = (ushort)concurrency
            };

            _logger.LogInformation("Attempting to start fake worker #{workerName}", name);

            try
            {
                var (clientWebSocket, serverWebSocket) = CreateWebSocketPair();

                var serverTask = RemoteWorkerService.AcceptHostAsync(
                    _logger,
                    _workerDistribution,
                    serverWebSocket,
                    _serverRpcRegistry,
                    CancellationToken.None);
                
                var clientTask = WorkerHost.StartAsync(
                    _implFactory,
                    _workerRpcRegistry,
                    settings,
                    clientWebSocket,
                    CancellationToken.None);

                await serverTask.ConfigureAwait(false);
                await clientTask.ConfigureAwait(false);

                _logger.LogInformation(
                    "Successfully started fake worker #{workerName}",
                    name);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to start fake worker #{workerName}", name);
            }
        }
    }
}
