using System;
using System.Diagnostics.Metrics;

namespace Hearty.Server;

/// <summary>
/// Collects metrics related to job management, queuing and scheduling.
/// </summary>
public class JobServerMetrics
{
    /// <summary>
    /// Counts the number of macro jobs successfully enqueued.
    /// </summary>
    internal Counter<int> MacroJobsQueued { get; }

    /// <summary>
    /// Counts the number of macro jobs that have been taken out of the
    /// queue, but may still yet to be run.
    /// </summary>
    internal Counter<int> MacroJobsDequeued { get; }

    /// <summary>
    /// Counts the number of micro jobs that have been queued.
    /// </summary>
    internal Counter<int> MicroJobsQueued { get; }

    /// <summary>
    /// Counts the number of micro jobs that have been tkane out of a
    /// job queue, but may still have yet to be run.
    /// </summary>
    internal Counter<int> MicroJobsProcessed { get; }

    internal Counter<int> CountJobsStarted { get; }

    internal Counter<int> CountJobsFinished { get; }

    /// <summary>
    /// Prepares instruments for collecting metrics as the job management
    /// system runs.
    /// </summary>
    /// <param name="meter">
    /// Where to register the metrics instruments. 
    /// </param>
    public JobServerMetrics(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);

        MacroJobsQueued = meter.CreateCounter<int>(
                                name: "macro-jobs",
                                unit: "jobs",
                                description: "Number of macro jobs accepted");

        MacroJobsDequeued = meter.CreateCounter<int>(
                                name: "macro-jobs-dequeued",
                                unit: "jobs",
                                description: "Number of macro jobs de-queued");

        MicroJobsProcessed = meter.CreateCounter<int>(
                                name: "micro-jobs-dequeued",
                                unit: "jobs",
                                description: "Number of micro jobs de-queued");

        MicroJobsQueued = meter.CreateCounter<int>(
                                name: "micro-jobs",
                                unit: "jobs",
                                description: "Number of micro jobs accepted");

        CountJobsStarted = meter.CreateCounter<int>(
                                name: "jobs-started",
                                unit: "jobs",
                                description: "Number of jobs launched on workers");

        CountJobsFinished = meter.CreateCounter<int>(
                                name: "jobs-finished",
                                unit: "jobs",
                                description: "Number of jobs ended after being launched");
    }

    /// <summary>
    /// Default instance of this class to record metrics into
    /// if another instance is not specified by the user of this library 
    /// upon constructing objects that expose any counters.
    /// </summary>
    internal static JobServerMetrics Default { get; } =
        new JobServerMetrics(new Meter("Hearty.Server"));
}
