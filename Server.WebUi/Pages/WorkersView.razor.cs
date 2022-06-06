using Hearty.Work;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.WebUi.Pages;

/// <summary>
/// Blazor page listing the current worker hosts connected to the job server.
/// </summary>
public partial class WorkersView
{
    /// <inheritdoc />
    protected override void OnInitialized()
        => StartRefreshing(TimeoutBucket.After5Seconds);

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
        _displaySpecialization.TestWorkersGenerator is not null;

    /// <summary>
    /// Create fake worker hosts connecting to a WebSocket endpoint
    /// for job distribution from a Hearty server.
    /// </summary>
    private Task GenerateWorkersAsync()
    {
        int count = InputNumberOfWorkers;
        int concurrency = InputConcurrency;
        return _displaySpecialization.TestWorkersGenerator!.GenerateWorkersAsync(count, concurrency);
    }
}
