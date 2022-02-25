using System;

namespace Hearty.Scheduling
{
    /// <summary>
    /// Represents an object that acts as a scheduling flow
    /// for fair scheduling.
    /// </summary>
    /// <typeparam name="TMessage">
    /// The type of job or message to be delivered by fair scheduling.
    /// </typeparam>
    public interface ISchedulingFlow<T>
    {
        /// <summary>
        /// Obtain the scheduling flow that can be put with
        /// other flows inside some parent scheduling group.
        /// </summary>
        /// <returns>
        /// Object implementing the scheduling flow.  Callers may
        /// implicitly assume that it is always the same reference
        /// for the lifetime of this instance.  This information is 
        /// not a property to allow the scheduling flow object to be
        /// lazily initialized.
        /// </returns>
        SchedulingFlow<T> AsFlow();
    }
}
