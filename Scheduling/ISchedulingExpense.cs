using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Scheduling;

/// <summary>
/// Reports the initial charge for an abstract item in fair scheduling.
/// </summary>
public interface ISchedulingExpense
{
    /// <summary>
    /// Get the amount that this item should be charged
    /// when it is taken out of its scheduling flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a method and not a property to allow the
    /// implementing object to mutate itself to record the
    /// charge that is being taken.  The charge may well vary
    /// based on environmental conditions.  
    /// </para>
    /// <para>
    /// An implementation of <see cref="SchedulingFlow{T}" />
    /// that uses this interfaces on its items shall call
    /// this method exactly once when an item is de-queued.
    /// This method should not throw exceptions; otherwise
    /// the item may be lost.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The amount of charge, measured in the same units
    /// as in <see cref="SchedulingFlow{T}.TryTakeItem(out T, out int)" />.
    /// </returns>
    int GetInitialCharge();
}
