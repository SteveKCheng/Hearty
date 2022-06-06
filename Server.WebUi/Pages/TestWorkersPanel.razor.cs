using Hearty.Work;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.WebUi.Pages;

/// <summary>
/// Blazor page listing the current worker hosts connected to the job server.
/// </summary>
public partial class TestWorkersPanel
{
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
    /// Create fake worker hosts connecting to a WebSocket endpoint
    /// for job distribution from a Hearty server.
    /// </summary>
    private Task GenerateWorkersAsync()
    {
        int count = InputNumberOfWorkers;
        int concurrency = InputConcurrency;
        return _testWorkersGenerator.GenerateWorkersAsync(count, concurrency);
    }
}
