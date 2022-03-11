using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server
{
    /// <summary>
    /// A list of items that can be added to incrementally,
    /// with concurrent producers and consumers.
    /// </summary>
    /// <typeparam name="T">
    /// The type of item to be stored in the list.
    /// </typeparam>
    /// <remarks>
    /// <para>
    /// The distinguishing characteristic of this class is that
    /// producers may compete to produce the same data.  In
    /// certain server-based applications, multiple producers
    /// cannot be unified into one because they may run at
    /// different priorities, e.g. when started by different
    /// users.  Multiple producers must still produce the
    /// same list of items though, and the items must be
    /// produced in sequence.
    /// </para>
    /// <para>
    /// Also, the members that has been produced so far
    /// can be asynchronously consumed even when the whole
    /// list has not been produced yet.  Consumers can
    /// access already-produced elements by index.
    /// </para>
    /// <para>
    /// This class implements something analogous to a channel,
    /// but consumers can randomly access the produced elements 
    /// by index, and multiple producers can "compete"
    /// (whichever first produces the item at a particular index 
    /// "wins").
    /// </para>
    /// </remarks>
    public class IncrementalAsyncList<T> : IAsyncEnumerable<T>
    {
        /// <summary>
        /// The list of members that may be incrementally populated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the members with index 0 to <see cref="_membersCount" /> 
        /// minus one are valid in this array.  The slots towards
        /// the end of the array are for new elements that may be
        /// added later.
        /// </para>
        /// <para>
        /// The list is resized by replacing this variable with a 
        /// new array reference.  Since no locks are taken on 
        /// reading existing members, when the list is resized,
        /// the old array may still be concurrently read while
        /// the new array is active.  But that is harmless since
        /// existing members are guaranteed not to change.
        /// </para>
        /// </remarks>
        private volatile T[] _members;

        /// <summary>
        /// The number of members that have been populated so far.
        /// </summary>
        /// <remarks>
        /// This variable must be updated only after the member
        /// has been populated in <see cref="_members" />.
        /// When a member has already been stored,
        /// no locks are taken in order to read it out.
        /// </remarks>
        private volatile int _membersCount;

        /// <summary>
        /// The asynchronous tasks that are waiting for a member 
        /// to be populated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When <see cref="TryGetMemberAsync(int, CancellationToken)" /> is asked to
        /// read a member yet to be populated in an incomplete list,
        /// it asynchronously waits by populating the task for
        /// the desired index into this dictionary. The task is completed
        /// when the member is populated.  Multiple calls to that method
        /// with the same index will share (and set continuations onto) 
        /// the same task object.
        /// </para>
        /// <para>
        /// This object is also locked to coordinate mutating variables
        /// between concurrent producers and consumers.  When the list
        /// is terminated, this object is set to null.  
        /// </para>
        /// </remarks>
        private volatile Dictionary<int, AsyncTaskMethodBuilder<(T, bool)>>? _consumers = new();

        /// <summary>
        /// Any user-supplied exception that is supplied on completing 
        /// the list.
        /// </summary>
        /// <remarks>
        /// This feature enables a background task that is producing the
        /// sequence to propagate errors it encounters 
        /// in producing the sequence.
        /// </remarks>
        private Exception? _exception;

        /// <summary>
        /// Builds the task for the <see cref="Completion" /> property.
        /// </summary>
        private AsyncTaskMethodBuilder _completionBuilder;

        /// <summary>
        /// Set to true if <see cref="_completionBuilder" /> has been initialized.
        /// </summary>
        private bool _completionBuilderInUse;

        /// <summary>
        /// Throw an exception if the argument index is negative.
        /// </summary>
        private static void ThrowIfIndexIsNegative(int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(index),
                    message: "The index to a promise list cannot be negative. ");
            }
        }

        /// <summary>
        /// Prepare to build the incremental list.
        /// </summary>
        /// <param name="capacity">
        /// Capacity that the array backing the list
        /// is initially allocated for.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="capacity" /> is negative.
        /// </exception>
        public IncrementalAsyncList(int capacity = 16)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(capacity),
                    message: "Initial capacity cannot be negative. ");
            }

            _members = (capacity > 0) ? new T[capacity] 
                                      : Array.Empty<T>();
        }

        /// <summary>
        /// Get a member of the list by index if it exists,   
        /// asynchronously.
        /// </summary>
        /// <param name="index">
        /// The index of the desired member.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel waiting for a member that
        /// has yet to be populated.
        /// </param>
        /// <returns>
        /// Asynchronous task that completes with the desired
        /// member of the list, as the first item of the tuple.
        /// The second item of the tuple is true if the index
        /// refers to an actual member, or false if the index
        /// is past the end of the list.  In the latter case
        /// the first item of the tuple is set to the default
        /// value for <typeparamref name="T" />.  If the
        /// list has been terminated with an exception, that
        /// exception is not thrown out of this method, and
        /// all preceding members remain available.
        /// </returns>
        public ValueTask<(T Value, bool IsValid)> 
            TryGetMemberAsync(int index,
                              CancellationToken cancellationToken = default)
            => TryGetMemberAsync(index, Timeout.InfiniteTimeSpan, cancellationToken);

        /// <summary>
        /// Get a member of the list by index if it exists,   
        /// asynchronously.
        /// </summary>
        /// <param name="index">
        /// The index of the desired member.
        /// </param>
        /// <param name="timeout">
        /// Timeout for waiting for a member.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel waiting for a member that
        /// has yet to be populated.
        /// </param>
        /// <returns>
        /// Asynchronous task that completes with the desired
        /// member of the list, as the first item of the tuple.
        /// The second item of the tuple is true if the index
        /// refers to an actual member, or false if the index
        /// is past the end of the list.  In the latter case
        /// the first item of the tuple is set to the default
        /// value for <typeparamref name="T" />. If the
        /// list has been terminated with an exception, that
        /// exception is not thrown out of this method, and
        /// all preceding members remain available.
        /// </returns>
        public ValueTask<(T Value, bool IsValid)> 
            TryGetMemberAsync(int index, 
                              TimeSpan timeout,
                              CancellationToken cancellationToken)
        {
            ThrowIfIndexIsNegative(index);
            cancellationToken.ThrowIfCancellationRequested();

            var consumers = _consumers;
            var count = _membersCount;

            ValueTask<(T, bool)> GetMemberImmediately(int index, int count)
            {
                bool valid = index < count;
                T value = valid ? _members[index] : default!;
                return ValueTask.FromResult((value, valid));
            }

            // Opportunistically read without taking locks
            if (consumers is null || index < count)
                return GetMemberImmediately(index, count);

            Task<(T, bool)> task;

            lock (consumers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                count = _membersCount;

                // Need to re-check after taking lock
                if (_consumers is null)
                    return GetMemberImmediately(index, count);

                // Re-use existing task or create new task
                ref var taskBuilder = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                        consumers,
                                        index,
                                        out _);
                task = taskBuilder.Task;
            }

            // Add timeout and cancellation token on top of the
            // shared task, if any.
            if (!task.IsCompleted &&
                (cancellationToken.CanBeCanceled || 
                 timeout != Timeout.InfiniteTimeSpan))
            {
                if (timeout == TimeSpan.Zero)
                    throw new TimeoutException("Requested timeout expired before a member of the list becomes available. ");

                task = task.WaitAsync(timeout, cancellationToken);
            }

            return new ValueTask<(T, bool)>(task);
        }

        /// <summary>
        /// Attempt to add a member to the list.
        /// </summary>
        /// <param name="index">
        /// The index of the member in the list.  Members can
        /// only be added at the end of the list, but the index
        /// is passed to allow multiple producers trying to 
        /// set the members independently, but always in sequence.  
        /// Thus each producer must maintain an incrementing
        /// integer counter to pass in for this argument.
        /// </param>
        /// <param name="value">
        /// The value of the member to set.
        /// </param>
        /// <returns>
        /// True if the member has been added successfully.
        /// False if another producer has already added an
        /// item at the same index; then <paramref name="value" />
        /// is not added to the list.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">
        /// <paramref name="index" /> is more than one past
        /// the current last index of the list.  That is,
        /// setting the desired member would create a hole
        /// in the list which is not allowed.
        /// </exception>
        public bool TrySetMember(int index, T value)
        {
            ThrowIfIndexIsNegative(index);

            // Ignore if list is already completed
            var consumers = _consumers;
            if (consumers is null)
                return false;

            bool hasTaskBuilder;
            AsyncTaskMethodBuilder<(T, bool)> taskBuilder;

            lock (consumers)
            {
                var count = _membersCount;

                if (_consumers is null || index < count)
                    return false;

                if (index > count)
                {
                    throw new IndexOutOfRangeException(
                        "A new index to register a new promise must " +
                        "immediately follow the last highest index. ");
                }

                var members = _members;

                // Resize array if it is not sufficient
                if (members.Length <= index)
                {
                    var newLength = checked(Math.Max(members.Length, 1) * 2);
                    var newMembers = new T[newLength];
                    members.CopyTo(newMembers, 0);
                    _members = members = newMembers;
                }

                members[index] = value;
                _membersCount = count + 1;

                hasTaskBuilder = consumers.Remove(index, out taskBuilder);
            }

            // Invoke continuations outside the lock
            if (hasTaskBuilder)
                taskBuilder.SetResult((value, true));

            return true;
        }

        /// <summary>
        /// Attempt to terminate the list.
        /// </summary>
        /// <remarks>
        /// Terminating the list means that <see cref="TryGetMemberAsync(int, CancellationToken)" />
        /// no longer waits for a member to be populated if it is not
        /// already there.
        /// </remarks>
        /// <param name="count">
        /// The number of items that should have been produced so far.
        /// This argument is passed in purely to check for errors.
        /// </param>
        /// <param name="exception">
        /// Any exception to record as part of completion.
        /// </param>
        /// <returns>
        /// True if the current invocation of this method
        /// is first to successfully terminate list.
        /// False if the list has already been terminated
        /// (by another producer).
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="count" /> does not match the
        /// number of unique indices whose corresponding values
        /// have been set.
        /// </exception>
        public bool TryComplete(int count, Exception? exception = null)
        {
            // Ignore if list is already completed
            var consumers = _consumers;
            if (consumers is null)
                return false;

            bool completionBuilderInUse = false;
            AsyncTaskMethodBuilder completionBuilder = default;

            lock (consumers)
            {
                if (_membersCount != count)
                {
                    throw new InvalidOperationException(
                        "The count of promises when marking a promise list " +
                        "for completion does not match the current number " +
                        "promises that have been registered. ");
                }

                if (_consumers is null)
                    return false;

                _exception = exception;

                if (_completionBuilderInUse)
                {
                    completionBuilder = _completionBuilder;
                    completionBuilderInUse = true;
                }

                _consumers = null;
            }

            // Notify tasks waiting for members past the end of the list.
            // Invoke continuations outside the lock.
            foreach (var (_, taskBuilder) in consumers)
                taskBuilder.SetResult((default!, false));

            // Notify tasks waiting on the Completion property.
            if (completionBuilderInUse)
            {
                if (exception is null)
                {
                    completionBuilder.SetResult();

                    // Do not need to keep the builder around
                    // if there is no exception.
                    _completionBuilderInUse = false;
                    _completionBuilder = default;
                }
                else
                {
                    completionBuilder.SetException(exception);
                }
            }

            return true;
        }

        /// <summary>
        /// Get a member of the list, which must exist.
        /// </summary>
        /// <param name="index">
        /// The index of the member in the list, numbered from 0
        /// to <see cref="Count" /> minus one.
        /// </param>
        /// <returns>
        /// The desired member stored in the list.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">
        /// A member with the given index does not currently
        /// exist in the list.
        /// </exception>
        public T this[int index]
        {
            get
            {
                ThrowIfIndexIsNegative(index);

                var count = _membersCount;
                if (index >= count)
                {
                    throw new IndexOutOfRangeException(
                        "The index to the promise list does not point " +
                        "to a valid member. ");
                }

                return _members[index];
            }
        }

        /// <summary>
        /// Whether the list has been conclusively terminated
        /// by a producer.
        /// </summary>
        public bool IsComplete => _consumers is null;

        /// <summary>
        /// The current number of items in the list.
        /// </summary>
        public int Count => _membersCount;

        /// <summary>
        /// The exception that the list has been terminated with, if any.
        /// </summary>
        public Exception? Exception => _exception;

        /// <summary>
        /// A task that completes when the list is terminated.
        /// </summary>
        /// <remarks>
        /// If the list is terminated with an associated exception,
        /// then this task will be faulted with that exception.
        /// </remarks>
        public Task Completion
        {
            get
            {
                // Get an incomplete task object if the list is not yet complete.
                var consumers = _consumers;
                if (consumers is not null)
                {
                    lock (consumers)
                    {
                        if (_consumers is not null)
                        {
                            var task = _completionBuilder.Task;
                            _completionBuilderInUse = true;
                            return task;
                        }
                    }
                }

                //
                // Execution reaches here if the list is already complete.
                //

                var exception = _exception;
                if (exception is null)
                    return Task.CompletedTask;

                if (Volatile.Read(ref _completionBuilderInUse))
                    return _completionBuilder.Task;

                var builder = new AsyncTaskMethodBuilder();
                builder.SetException(exception);

                // Cache the Task object holding the exception.
                // We assume that struct tearing on _completionBuilder
                // is harmless because it only holds a single reference.
                _completionBuilder = builder;
                Volatile.Write(ref _completionBuilderInUse, true);

                return builder.Task;
            }
        }

        /// <inheritdoc cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)" />
        public async IAsyncEnumerator<T> 
            GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            int index = 0;
            while (true)
            {
                var (item, valid) = await TryGetMemberAsync(index, cancellationToken)
                                            .ConfigureAwait(false);
                if (!valid)
                    break;

                ++index;
                yield return item;
            }

            if (Exception is Exception exception)
                throw exception;
        }
    }
}
