using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

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
    /// <param name="exception">
    /// An error condition to associate with the termination
    /// of the list of promises, if any.
    /// </param>
    /// <param name="cancellationToken">
    /// A cancellation token to observe (asynchronously) before 
    /// the promise list is actually terminated.  If it is
    /// cancelled before then, and <paramref name="exception" />
    /// is not null, then <see cref="OperationCanceledException" />
    /// with this token will be set as the termination exception.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="count" /> does not match the
    /// number of unique indices whose corresponding values
    /// have been set.
    /// </exception>
    /// <returns>
    /// True if the current producer is the first to terminate.
    /// </returns>
    bool TryComplete(int count,
                     Exception? exception = null,
                     CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the list has been conclusively terminated
    /// by any producer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property may be consulted to stop a producer
    /// early once another producer has already terminated 
    /// the list, to avoid doing useless work.
    /// </para>
    /// <para>
    /// This property may return false even after <see cref="TryComplete" />
    /// has been called if completion is asynchronous.
    /// </para>
    /// </remarks>
    bool IsComplete { get; }

    /// <summary>
    /// Wait asynchronously until the list is terminated
    /// and all promises have completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the list is terminated with an associated exception,
    /// then the returned task will still not fault.
    /// This method lets the caller wait to perform clean-up
    /// actions until all promises finish.  And that clean-up
    /// should happen even if list terminated with an exception.
    /// </para>
    /// <para>
    /// The returned task may not complete even after 
    /// <see cref="TryComplete" />
    /// when any promise that had been set earlier by
    /// <see cref="SetMember" /> has not yet completed.
    /// </para>
    /// </remarks>
    ValueTask WaitForAllPromisesAsync();

    /// <summary>
    /// Get the object that can be used to read what has
    /// been built by this object, as a promise output.
    /// </summary>
    PromiseData Output { get; }
}
