using System;
using System.Threading.Tasks;

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
        /// <para>
        /// This property may be consulted to stop a producer
        /// once another producer has already terminated the list.
        /// </para>
        /// <para>
        /// This property may return false even after <see cref="TryComplete" />
        /// has been called if completion is asynchronous.
        /// </para>
        /// </remarks>
        bool IsComplete { get; }

        /// <summary>
        /// Whether this list being built has been terminated by
        /// cancellation.
        /// </summary>
        /// <remarks>
        /// The value of this property may not be fixed 
        /// until <see cref="IsComplete" /> is true.
        /// </remarks>
        bool IsCancelled { get; }

        /// <summary>
        /// Wait asynchronously until the list is terminated
        /// and all promises have completed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If the list is terminated with an associated exception,
        /// then the returned task will be faulted with that exception.
        /// </para>
        /// <para>
        /// The returned task may not complete even after 
        /// <see cref="TryComplete" />
        /// when any promise that had been set earlier by
        /// <see cref="SetMember" /> has not yet completed.
        /// This task can be waited upon before executing clean-up
        /// actions.
        /// </para>
        /// </remarks>
        ValueTask WaitForAllPromisesAsync();

        /// <summary>
        /// Get the object that can be used to read what has
        /// been built by this object, as a promise output.
        /// </summary>
        PromiseData Output { get; }
    }
}
