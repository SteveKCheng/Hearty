using Hearty.Work;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.WebUi.Pages;

/// <summary>
/// Blazor page listing the current worker hosts connected to the job server.
/// </summary>
public partial class Workers
{
    /// <inheritdoc />
    protected override void OnInitialized()
        => StartRefreshing(TimeoutBucket.After5Seconds);

    /// <summary>
    /// Running count of all the workers created so far,
    /// to generate unique names for them.
    /// </summary>
    private uint _countCreatedWorkers;

    /// <summary>
    /// User's input on the Blazor page for the "degree of concurrency"
    /// of each test host to instantiate.
    /// </summary>
    private int InputConcurrency { get; set; } = 4;

    /// <summary>
    /// User's input on the Blazor page for the "number of workers"
    /// to instantiate.
    /// </summary>
    private int InputNumberOfWorkers { get; set; } = 10;

    /// <summary>
    /// Returns true if the given URL is absolute and refers to
    /// WebSockets.
    /// </summary>
    private static bool IsWebSocketsUrl(Uri url)
        => url.IsAbsoluteUri &&
            (string.Equals(url.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(url.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True if the section of the page that creates mock worker
    /// hosts should be enabled.
    /// </summary>
    private bool IsHostCreationEnabled =>
        _displaySpecialization.WorkerFactory is not null;

    /// <summary>
    /// Create fake worker hosts connecting to a WebSocket endpoint
    /// for job distribution from a Hearty server.
    /// </summary>
    private async Task GenerateWorkersAsync()
    {
        var url = _displaySpecialization.GetWorkersWebSocketsUrl(_navigationManager);
        var workFactory = _displaySpecialization.WorkerFactory;

        if (url is null || workFactory is null)
            return;

        if (!IsWebSocketsUrl(url))
            throw new ArgumentException("Connection URL must be for the WebSockets protocol. ", nameof(url));

        int count = InputNumberOfWorkers;
        int concurrency = InputConcurrency;

        if (count <= 0)
            return;

        _logger.LogInformation("{count} fake worker(s) will connect by WebSockets on URL: {url}",
                               count, url);

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
                await WorkerHost.ConnectAndStartAsync(
                    workFactory.Invoke(settings),
                    settings,
                    url,
                    null,
                    CancellationToken.None);

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
