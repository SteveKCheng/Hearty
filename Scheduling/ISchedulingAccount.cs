using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Scheduling;

/// <summary>
/// Adjusts the "account balance" and tracks statistics as
/// items are processed from a scheduling flow.
/// </summary>
/// <remarks>
/// <para>
/// Each item has an abstract "charge" that may not be known upfront.
/// Usually the charge represents elapsed time, with the measurement
/// units decided by convention.  As items are processed, their charges
/// on the scheduling flow are recorded.
/// </para>
/// <para>
/// This abstract interface is designed to compose, i.e. for multiple
/// scheduling flows to be updated at the same time.
/// </para>
/// </remarks>
public interface ISchedulingAccount
{
    /// <summary>
    /// Update the charge incurred for an item that has not 
    /// completed.
    /// </summary>
    /// <param name="current">
    /// The current amount that the item has been charged for.
    /// Some implementations of this method require this value 
    /// to be able to update statistics efficiently.
    /// This value shall be null when a new incomplete item is
    /// to be introduced.
    /// </param>
    /// <param name="change">
    /// The change in the charge for the item.
    /// If <paramref name="current" /> is null, this value should
    /// match the charge, if any, for the item that was initially
    /// reported to the scheduling flow.
    /// </param>
    /// <remarks>
    /// Upon completion of an item, both this method and 
    /// <see cref="TabulateCompletedItem(int)"/> must be called
    /// </remarks>
    void UpdateCurrentItem(int? current, int change);

    /// <summary>
    /// Register an item that was obtained from the scheduling flow
    /// as completed, with a final account of the charge it incurred.
    /// </summary>
    /// <remarks>
    /// This method updates the statistics that are returned by
    /// <see cref="CompletionStatistics" />.
    /// </remarks>
    /// <param name="charge">
    /// The cumulative charge for one of the items (typically a job)
    /// that was processed from the scheduling flow.
    /// </param>
    void TabulateCompletedItem(int charge);

    /// <summary>
    /// Get the running statistics on the items that have been
    /// processed from the scheduling flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Processed" refers not only to that the items being
    /// de-queued but also having been delivered as messages or 
    /// executed as jobs.
    /// </para>
    /// <para>
    /// These statistics can be used for monitoring, but do not 
    /// directly affect the priority or rate that the scheduling 
    /// flow is drained at.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Current snapshot of the statistics.  
    /// </returns>
    SchedulingStatistics CompletionStatistics { get; }
}
