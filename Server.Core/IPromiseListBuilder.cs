using System;

namespace JobBank.Server
{
    /// <summary>
    /// Abstract interface to pass on or store
    /// an incrementally-generated sequence of promises.
    /// </summary>
    /// <remarks>
    /// This interface is used by macro jobs to store the
    /// list of promises they generate, so consumers can
    /// consult it.  A dummy implementation of this interface
    /// could also just discard the generated sequence,
    /// if the application does not need it.
    /// </remarks>
    public interface IPromiseListBuilder
    {
        /// <summary>
        /// Invoked by a producer to add an item to its list of promises.
        /// </summary>
        /// <param name="index">
        /// The index of the promise to add in the list.  Members can
        /// only be added at the end of the list, but the index
        /// is passed to allow multiple producers trying to 
        /// set the members independently, but always in sequence.  
        /// Thus each producer must maintain an incrementing
        /// integer counter to pass in for this argument.
        /// </param>
        /// <param name="promise">
        /// The promise to add to the list.
        /// </param>
        /// <exception cref="IndexOutOfRangeException">
        /// <paramref name="index" /> is more than one past
        /// the current last index of the list.  That is,
        /// setting the desired member would create a hole
        /// in the list which is not allowed.
        /// </exception>
        void SetMember(int index, Promise promise);

        /// <summary>
        /// Invoked by a producer to terminate its list of promises.
        /// </summary>
        /// <remarks>
        /// Terminating the list means that consumers no longer
        /// (asynchronously) wait for items at non-existent indices.
        /// </remarks>
        /// <param name="count">
        /// The number of items that should have been produced so far.
        /// This argument is passed in purely to check for errors.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="count" /> does not match the
        /// number of unique indices whose corresponding values
        /// have been set.
        /// </exception>
        /// <returns>
        /// True if the current producer is the first to terminate.
        /// </returns>
        bool TryComplete(int count, Exception? exception = null);

        /// <summary>
        /// Whether the list has been conclusively terminated
        /// by any producer.
        /// </summary>
        /// <remarks>
        /// This property may be consulted to stop a producer
        /// once another producer has already terminated the list.
        /// </remarks>
        bool IsComplete { get; }
    }
}
