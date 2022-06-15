using System;

namespace Hearty.Scheduling;

/// <summary>
/// Interface to report the status of a (remote) worker.
/// </summary>
public interface IWorkerNotification
{
    /// <summary>
    /// Event that fires when the status of a remote worker changes.
    /// </summary>
    event EventHandler<WorkerEventArgs>? OnEvent;

    /// <summary>
    /// Name that identifies this worker, for debugging and monitoring.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this worker is working normally and
    /// accepting jobs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If this worker represents a separate process, it may fail
    /// without the entire scheduling system failing. 
    /// When that happens the scheduling system should avoid
    /// submitting further jobs to the worker which would only
    /// fail.
    /// </para>
    /// <para>
    /// When this flag becomes false, the condition is 
    /// irreversible for the same object.  That is, the
    /// same object cannot be revived.  Doing so avoids 
    /// tricky and complicated coordination for revival
    /// between the object and and its consuming client, 
    /// whose implementations may execute concurrently.
    /// </para>
    /// <para>
    /// If this worker is detected to fail, this flag should be set to
    /// false before reporting the exception from any task returned
    /// by <see cref="IJobWorker{TInput, TOutput}.ExecuteJobAsync" />.  
    /// Thus, a client can check
    /// for a failed worker by consulting this flag after awaiting
    /// the completion of that method,
    /// without risk of false negatives. 
    /// </para>
    /// </remarks>
    bool IsAlive { get; }
}

/// <summary>
/// Specifies the kind of change of status of a worker
/// in the job distribution system.
/// </summary>
public enum WorkerEventKind
{
    /// <summary>
    /// The worker is getting fewer or more total abstract
    /// resources to execute jobs.
    /// </summary>
    AdjustTotalResources,

    /// <summary>
    /// The worker is shutting down.
    /// </summary>
    Shutdown,
}

/// <summary>
/// Describes a change of status of a worker
/// in the job distribution system.
/// </summary>
public class WorkerEventArgs
{
    /// <summary>
    /// Specifies the kind of change of status of a worker.
    /// </summary>
    public WorkerEventKind Kind { get; init; }

    /// <summary>
    /// If <see cref="Kind" /> is <see cref="WorkerEventKind.AdjustTotalResources" />,
    /// specifies the new amount of resources available.
    /// </summary>
    public int TotalResources { get; init; }
}
