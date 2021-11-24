using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Interface to report the status of a (remote) worker.
    /// </summary>
    public interface IWorkerNotification
    {
        /// <summary>
        /// Event that fires when the status of a remote worker changes.
        /// </summary>
        event EventHandler<WorkerEventArgs> EventHandler;
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
}
