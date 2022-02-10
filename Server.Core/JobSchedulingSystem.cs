using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JobBank.Scheduling;
using JobBank.Utilities;
using Microsoft.Extensions.Logging;

namespace JobBank.Server
{
    using JobMessage = ScheduledJob<PromisedWork, PromiseData>;
    using ClientQueue = SchedulingQueue<ScheduledJob<PromisedWork, PromiseData>>;
    using OwnerCancellation = KeyValuePair<IJobQueueOwner, CancellationSourcePool.Use>;
    using MacroJobExpansion = IAsyncEnumerable<(PromiseRetriever, PromisedWork)>;

    /// <summary>
    /// Schedules execution of promises using a hierarchy of job queues.
    /// </summary>
    /// <remarks>
    /// The systems for managing job queueing, distributing jobs to 
    /// (remote) workers, and holding the results as promises are 
    /// implemented orthogonally.  This class integrates them together 
    /// into a coherent service for applications to schedule execution 
    /// of promises.
    /// </remarks>
    public class JobSchedulingSystem
    {
        /// <summary>
        /// The hierarchy of job queues with priority scheduling.
        /// </summary>
        public PrioritizedQueueSystem<
                JobMessage, 
                ClientQueueSystem<JobMessage, IJobQueueOwner, ClientQueue>>
            PriorityClasses { get; }

        /// <summary>
        /// The set of workers that can accept jobs to execute
        /// as they come off the job queues.
        /// </summary>
        public WorkerDistribution<PromisedWork, PromiseData>
            WorkerDistribution { get; }

        /// <summary>
        /// Logs important operations performed by this instance.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Translates exceptions from executing a job into promise output.
        /// </summary>
        private readonly PromiseExceptionTranslator _exceptionTranslator;

        /// <summary>
        /// Prepare the system to schedule jobs and assign them to workers.
        /// </summary>
        /// <param name="logger">
        /// Receives log messages for significant events in the job scheduling
        /// system.
        /// </param>
        /// <param name="exceptionTranslator">
        /// Translates .NET exceptions when they occur as a result of 
        /// executing the work for promises.
        /// </param>
        /// <param name="countPriorities">
        /// The number of priority classes for jobs.  This is typically
        /// a constant for the application.  The actual weights for
        /// each priority class are dynamically adjustable.
        /// </param>        
        public JobSchedulingSystem(ILogger<JobSchedulingSystem> logger, 
                                   PromiseExceptionTranslator exceptionTranslator,
                                   WorkerDistribution<PromisedWork, PromiseData> workerDistribution,
                                   int countPriorities = 10)
        {
            _logger = logger;
            _exceptionTranslator = exceptionTranslator;

            PriorityClasses = new(countPriorities, 
                () => new ClientQueueSystem<JobMessage, IJobQueueOwner, ClientQueue>(
                    key => new ClientQueue(),
                    new SimpleExpiryQueue(60000, 20)));

            for (int i = 0; i < countPriorities; ++i)
                PriorityClasses.ResetWeight(priority: i, weight: (i + 1) * 10);

            WorkerDistribution = workerDistribution;

            _jobRunnerTask = JobScheduling.RunJobsAsync(
                                PriorityClasses.AsChannel(),
                                workerDistribution.AsChannel(),
                                CancellationToken.None);
        }

        /// <summary>
        /// Expiry queue used to update counters of elapsed time for fair
        /// job scheduling.
        /// </summary>
        private readonly SimpleExpiryQueue _timingQueue
            = new SimpleExpiryQueue(1000, 50);

        /// <summary>
        /// Task that de-queues jobs and dispatches them to workers.
        /// </summary>
        private readonly Task _jobRunnerTask;

        #region Registration of cancellation sources

        /// <summary>
        /// Track <see cref="CancellationTokenSource" /> that are
        /// registered together with the jobs that are not supplied
        /// with external cancellation tokens.
        /// </summary>
        private readonly Dictionary<PromiseId,
                                    SmallList<OwnerCancellation>>
            _cancellations = new();

        /// <summary>
        /// Unregister all cancellation sources that have been registered
        /// with a promise ID.  
        /// </summary>
        /// <remarks>
        /// This method should be called once a promise has finished
        /// processing, successful or not.
        /// </remarks>
        private void UnregisterCancellationUse(PromiseId promiseId)
        {
            SmallList<OwnerCancellation> list;
            lock (_cancellations)
            {
                if (!_cancellations.Remove(promiseId, out list))
                    return;
            }

            foreach (var item in list)
                item.Value.Dispose();
        }

        /// <summary>
        /// Create and register a pooled 
        /// cancellation source associated to a queue owner and promise ID.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The registration allows cancellation to occur without
        /// holding a reference to the <see cref="CancellationTokenSource" />,
        /// which is impossible for remote clients.
        /// See <see cref="TryCancelForClient" />.
        /// </para>
        /// <para>
        /// Note that callers should check if the promise is completed
        /// before and after calling this method.  If it completes before, 
        /// then cancellation registration should be skipped since the
        /// clean-up action of calling <see cref="UnregisterCancellationUse" />
        /// might have already occurred, and therefore the new registration
        /// would become dangling.  If the promise completes after, 
        /// the registration is dangling for the same reason and must be
        /// undone.
        /// </para>
        /// </remarks>
        /// <param name="owner">
        /// The queue owner to associate the cancellation source with.
        /// </param>
        /// <param name="promiseId">
        /// The ID of the promise whose job execution is being targeted 
        /// for potential cancellation.  
        /// </param>
        /// <param name="cancellation">
        /// Refers to the pooled cancellation source.  On return,
        /// this parameter is reset to indicate transfer of ownership
        /// into the registry.
        /// </param>
        private void RegisterCancellationUse(IJobQueueOwner owner,
                                             PromiseId promiseId,
                                             ref CancellationSourcePool.Use cancellation)
        {
            var item = new OwnerCancellation(owner, cancellation);

            lock (_cancellations)
            {
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                _cancellations, promiseId, out bool exists);

                if (exists)
                {
                    var a = new OwnerCancellation[list.Count + 1];
                    for (int i = 0; i < list.Count; ++i)
                        a[i] = list[i];
                    a[list.Count] = item;
                    list = new SmallList<OwnerCancellation>(a);
                }
                else
                {
                    list = new SmallList<OwnerCancellation>(item);
                }
            }

            cancellation = default;
        }

        /// <summary>
        /// Cancel a job that had been pushed earlier
        /// without an explicit cancellation token.
        /// </summary>
        /// <param name="owner">
        /// The owner that had requested the job earlier. 
        /// </param>
        /// <param name="promiseId">
        /// The ID of the promise associated to the job.
        /// </param>
        /// <returns>
        /// False if the combination of client and promise is no longer
        /// registered.  True if the combination is registered
        /// and cancellation has been requested.
        /// </returns>
        public bool TryCancelForClient(IJobQueueOwner owner, PromiseId promiseId)
        {
            CancellationTokenSource? source = null;

            lock (_cancellations)
            {
                if (_cancellations.TryGetValue(promiseId, out var oldList))
                {
                    foreach (var item in oldList)
                    {
                        if (item.Key.Equals(owner))
                        {
                            source = item.Value.Source;
                            break;
                        }
                    }
                }
            }

            if (source is null)
                return false;

            source.Cancel();
            return true;
        }

        #endregion

        #region Micro jobs

        /// <summary>
        /// Cached "future" objects for jobs that have been submitted to at
        /// least one job queue.
        /// </summary>
        /// <remarks>
        /// This dictionary ensures that for a given promise there is at
        /// most one job object associated to it.  If the promise is
        /// submitted multiple times as jobs then all the jobs share
        /// the same job object.
        /// </remarks>
        private readonly Dictionary<PromiseId,
                            (SharedFuture<PromisedWork, PromiseData> Future, 
                             bool OwnsCancellation)>
            _microPromises = new();

        /// <summary>
        /// Remove the entry mapping a promise ID to a 
        /// scheduled (non-macro) job.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method is to be called once the job is finished
        /// to avoid unbounded growth in the mapping table.
        /// </para>
        /// <para>
        /// The mapping table manipulated by this method is not used
        /// for macro jobs, and so this method shall not be used
        /// for those.
        /// </para>
        /// </remarks>
        /// <param name="promiseId">
        /// The promise ID to unregister.
        /// </param>
        private void RemoveCachedFuture(PromiseId promiseId)
        {
            bool unregisterCancellation;
            lock (_microPromises)
            {
                bool isRemoved = _microPromises.Remove(promiseId, out var removedEntry);
                unregisterCancellation = isRemoved && removedEntry.OwnsCancellation;
            }

            if (unregisterCancellation)
                UnregisterCancellationUse(promiseId);
        }

        /// <summary>
        /// Throw an exception if the promise object returned from
        /// <see cref="PromiseRetriever" /> is the same as the last iteration.
        /// </summary>
        /// <remarks>
        /// This guards against coding mistakes leading to infinite loops 
        /// in the retry loop when scheduling shared jobs.
        /// </remarks>
        /// <param name="newPromise">The return value from the new
        /// invocation of <see cref="PromiseRetriever" />. </param>
        /// <param name="oldId">The ID of the promise from the invocation
        /// before.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// If the ID from <paramref name="newPromise" /> is the same as
        /// <paramref name="oldId" />.
        /// </exception>
        private static void VerifyPromiseIsDifferent(Promise newPromise, PromiseId oldId)
        {
            if (newPromise.Id == oldId)
            {
                throw new InvalidOperationException(
                    "A promise with a new ID is not being supplied from PromiseRetrieval " +
                    "when the preceding ID cannot be used any longer. ");
            }
        }

        /// <summary>
        /// Get a scheduled job entry to push into a client's queue,
        /// and set a promise to receive the result of the job.
        /// </summary>
        /// <param name="account">Scheduling account corresponding to
        /// the client's queue. </param>
        /// <param name="promiseRetriever">
        /// Callback to the user's code to obtain the promise which 
        /// should receive the results from the job.  The desired 
        /// <see cref="Promise" /> object cannot be passed down directly, 
        /// because in the rare case that an existing job raced to cancel, 
        /// the promise object needs to be refreshed in a retry loop.
        /// </param>
        /// <param name="work">Input for the job to execute,
        /// on first creation.  If there is already a shared
        /// job object for the associated promise ID, this input
        /// is effectively ignored.  
        /// </param>
        /// <param name="cancellationToken">
        /// A token that may be used to cancel the job,
        /// but only for the current client.  If the job
        /// is shared then all clients must cancel be the job
        /// is cancelled.
        /// </param>
        /// <param name="promise">
        /// On return, this parameter is set to point to 
        /// the new promise object, obtained from 
        /// the last call to <paramref name="promiseRetriever" />.
        /// </param>
        /// <remarks>
        /// <para>
        /// The future object may be shared if the same job
        /// has been pushed before (for another client).
        /// This sharing is accomplished by this method
        /// storing the relevant details into a table, keyed by 
        /// <paramref name="promiseId" />.
        /// </para>
        /// <para>
        /// This method is only used for non-macro jobs.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The job message that the caller should arrange
        /// to be put into the job queue, unless the promise
        /// has already completed, in which case the return
        /// value is null.
        /// </returns>
        internal JobMessage?
            RegisterJobMessage(ISchedulingAccount account, 
                               PromiseRetriever promiseRetriever,
                               in PromisedWork work, 
                               bool ownsCancellation,
                               CancellationToken cancellationToken,
                               out Promise promise)
        {
            JobMessage message;
            SharedFuture<PromisedWork, PromiseData> future;
            Task<PromiseData>? outputTask;

            promise = promiseRetriever.Invoke(work);

            do
            {
                var promiseId = promise.Id;

                // Do not register any job if the promise is already complete
                if (promise.IsCompleted)
                    return null;

                lock (_microPromises)
                {
                    ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(
                                        _microPromises, promiseId);

                    // Attach account to existing job for same promise ID if it exists
                    // and has not been cancelled.
                    if (!Unsafe.IsNullRef(ref entry))
                    {
                        future = entry.Future;

                        // Set flag when at least one client owns cancellation
                        ownsCancellation |= entry.OwnsCancellation;

                        if (future.TryShareJob(account, cancellationToken)
                            is JobMessage existingMessage)
                        {
                            outputTask = null;
                            entry.OwnsCancellation = ownsCancellation;
                            message = existingMessage;
                            break;
                        }

                        // If other clients raced to cancel the job, and they
                        // manage to do so before the new client attaches, 
                        // then ignore the existing job, and create a new
                        // promise object.
                        //
                        // The existing job will get cleaned up by the 
                        // "post action" passed to TryAwaitAndPostResult 
                        // below, when the cancellation propagates through
                        // to future.OutputTask, so the existing entry in
                        // the _microPromises dictionary should be left alone.
                        else
                        {
                            promise = promiseRetriever.Invoke(work.ReplacePromise(null));
                            VerifyPromiseIsDifferent(promise, promiseId);
                            continue;
                        }
                    }

                    // Usual case: completely new job
                    message = SharedFuture<PromisedWork, PromiseData>.CreateJob(
                            work,
                            work.InitialWait,
                            account,
                            cancellationToken,
                            _timingQueue,
                            out future);

                    outputTask = future.OutputTask;
                    _microPromises[promiseId] = (future, ownsCancellation);
                    break;
                }
            } while (true);

            // Only attach outputTask to the promise if this is a new job
            if (outputTask is not null)
            {
                bool awaiting = promise.TryAwaitAndPostResult(
                                    new ValueTask<PromiseData>(outputTask),
                                    _exceptionTranslator,
                                    p => RemoveCachedFuture(p.Id));

                // awaiting == false should not happen unless some code outside
                // of this class is posting to the promise.  In that case, recover
                // by reversing our registration.  Since the job message has not
                // been queued yet, there should be no harm.
                if (!awaiting)
                {
                    RemoveCachedFuture(promise.Id);
                    return null;
                }
            }
            else
            {
                // Check again if the promise completed, and if so,
                // the caller should not enqueue the message.
                if (promise.IsCompleted)
                    return null;
            }

            return message;
        }

        /// <summary>
        /// Create and push a job to complete a promise.
        /// </summary>
        /// <param name="owner">
        /// Owner of the queue.
        /// </param>
        /// <param name="priority">
        /// The desired priority that the job should be enqueued into.  It is expressed
        /// using the same integer key as used by <see cref="PriorityClasses" />.
        /// </param>
        /// <param name="promiseRetriever">
        /// Callback to the user's code to obtain the promise which 
        /// should receive the results from the job.  The desired 
        /// <see cref="Promise" /> object cannot be passed down directly, 
        /// because in the rare case that an existing job raced to cancel, 
        /// the promise object needs to be refreshed in a retry loop.
        /// </param>
        /// <param name="work">
        /// Describes the work to do in the scheduled job, 
        /// to produce the output for the promise.  This argument
        /// will effectively be ignored if the promise already
        /// has a job associated with it.
        /// </param>
        /// <param name="cancellationToken">
        /// Used by the caller to request cancellation of the job.
        /// </param>
        public Promise PushJob(IJobQueueOwner owner,
                               int priority,
                               PromiseRetriever promiseRetriever,
                               in PromisedWork work,
                               CancellationToken cancellationToken)
        {
            var queue = PriorityClasses[priority].GetOrAdd(owner);

            var message = RegisterJobMessage(
                            queue, promiseRetriever, work,
                            ownsCancellation: false,
                            cancellationToken,
                            out var promise);

            if (message is not null)
                queue.Enqueue(message.GetValueOrDefault());

            return promise;
        }

        /// <summary>
        /// Create and push a job to complete a promise,
        /// and also create and track a cancellation token for it.
        /// </summary>
        /// <remarks>
        /// This method is designed for clients that do not or
        /// cannot hold on to the <see cref="CancellationTokenSource" />
        /// needed to cancel a job, e.g. if the request is coming
        /// from a ReST API endpoint that may not necessarily retain
        /// any connection state.  The job can be cancelled instead
        /// via <see cref="TryCancelForClient" />.
        /// </remarks>
        /// <param name="owner">
        /// Owner of the queue.
        /// </param>
        /// <param name="priority">
        /// The desired priority that the job should be enqueued into.  It is expressed
        /// using the same integer key as used by <see cref="PriorityClasses" />.
        /// </param>
        /// <param name="promiseRetriever">
        /// Callback to the user's code to obtain the promise which 
        /// should receive the results from the job.  The desired 
        /// <see cref="Promise" /> object cannot be passed down directly, 
        /// because in the rare case that an existing job raced to cancel, 
        /// the promise object needs to be refreshed in a retry loop.
        /// </param>
        /// <param name="work">
        /// Describes the work to do in the scheduled job, 
        /// to produce the output for the promise.  This argument
        /// will effectively be ignored if the promise already
        /// has a job associated with it.
        /// </param>
        public Promise PushJobAndOwnCancellation(IJobQueueOwner owner,
                                                 int priority,
                                                 PromiseRetriever promiseRetriever,
                                                 in PromisedWork work)
        {
            var queue = PriorityClasses[priority].GetOrAdd(owner);

            var cancellation = CancellationSourcePool.Rent();

            var message = RegisterJobMessage(
                            queue, promiseRetriever, work,
                            ownsCancellation: true,
                            cancellation.Token,
                            out var promise);

            if (promise.IsCompleted)
            {
                cancellation.Dispose();
            }
            else
            {
                RegisterCancellationUse(owner, promise.Id, ref cancellation);

                // Undo if the promise could have raced to complete and clean up
                if (promise.IsCompleted)
                    UnregisterCancellationUse(promise.Id);
            }

            if (message is not null)
                queue.Enqueue(message.GetValueOrDefault());

            return promise;
        }

        #endregion

        #region Macro jobs

        /// <summary>
        /// Cached output for macro jobs.
        /// </summary>
        /// <remarks>
        /// This dictionary ensures that for a given promise of a macro
        /// job there is at most one promise output object associated to it.  
        /// Unlike micro jobs, the job object is not shared across
        /// multiple submissions of the same macro job, but the promise
        /// output is.  The count of jobs is maintained so cancelling
        /// a macro job does not cancel the promise output unless there
        /// are no more macro jobs sharing the same output object.
        /// </remarks>
        private readonly Dictionary<PromiseId,
                (IPromiseListBuilder ResultBuilder, 
                 int ProducerCount,
                 bool OwnsCancellation)>
            _macroPromises = new();

        /// <summary>
        /// Unregister the macro job/promise when it has finished expanding.
        /// </summary>
        /// <param name="promiseId">The promise ID for the macro job
        /// as passed to <see cref="PushMacroJob" />, which registered
        /// it.
        /// </param>
        /// <param name="toCancel">
        /// If true, this method is called to process a job cancellation,
        /// and it would not remove the registration entry if the same
        /// output is being written by other non-cancelled macro job
        /// instances.  If false, the registration entry is unconditionally
        /// removed.
        /// </param>
        /// <returns>
        /// Whether the registration entry existed and has been removed.
        /// </returns>
        internal bool UnregisterMacroJob(PromiseId promiseId, bool toCancel)
        {
            bool RemoveMainEntry(PromiseId promiseId, 
                                 bool toCancel, 
                                 out bool unregisterCancellation)
            {
                unregisterCancellation = false;

                lock (_macroPromises)
                {
                    if (toCancel)
                    {
                        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(
                                            _macroPromises, promiseId);

                        // Do not remove the entry from the dictionary if the
                        // same output is being written by another macro job instance.
                        if (Unsafe.IsNullRef(ref entry) || --entry.ProducerCount > 0)
                            return false;
                    }

                    bool isRemoved = _macroPromises.Remove(promiseId, out var removedEntry);
                    unregisterCancellation = isRemoved && removedEntry.OwnsCancellation;
                    return isRemoved;
                }
            }

            bool isRemoved = RemoveMainEntry(promiseId, toCancel, 
                                             out bool unregisterCancellation);
            if (unregisterCancellation)
                UnregisterCancellationUse(promiseId);

            return isRemoved;
        }

        /// <summary>
        /// Re-factored code for pushing a macro job with
        /// or without its own cancellation source.
        /// </summary>
        internal Promise PushMacroJobCore(IJobQueueOwner owner,
                                          int priority,
                                          PromiseRetriever promiseRetriever,
                                          PromisedWork work,
                                          PromiseListBuilderFactory builderFactory,
                                          MacroJobExpansion expansion,
                                          bool ownsCancellation,
                                          CancellationToken cancellationToken,
                                          out IPromiseListBuilder? resultBuilder)
        {
            var queue = PriorityClasses[priority].GetOrAdd(owner);
            bool isNewJob;

            var promise = promiseRetriever.Invoke(work);

            do
            {
                isNewJob = false;

                lock (_macroPromises)
                {
                    var promiseId = promise.Id;
                    ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                        _macroPromises, promiseId, out _);

                    if (entry.ResultBuilder is null)
                    {
                        // Not re-using existing result builder
                        entry.ResultBuilder = builderFactory.Invoke(promise);
                        isNewJob = true;
                    }
                    else if (entry.ResultBuilder.IsCancelled)
                    {
                        // Cannot re-use existing result builder because
                        // it has been cancelled.
                        promise = promiseRetriever.Invoke(work.ReplacePromise(null));
                        VerifyPromiseIsDifferent(promise, promiseId);
                        continue;
                    }
                    else if (entry.ResultBuilder.IsComplete)
                    {
                        // No need to register any job if sharing an existing
                        // promise and the results builder is already complete.
                        resultBuilder = null;
                        return promise;
                    }

                    resultBuilder = entry.ResultBuilder;
                    ++entry.ProducerCount;

                    // Set flag when at least one client owns cancellation
                    entry.OwnsCancellation |= ownsCancellation;

                    break;
                }
            } while (true);

            bool toEnqueue = true;
            if (isNewJob)
            {
                // Need to set the result builder into the promise for a new job
                toEnqueue = promise.TryAwaitAndPostResult(
                                    ValueTask.FromResult(resultBuilder.Output),
                                    _exceptionTranslator);
            }

            // A (shared) result builder could have raced to complete.
            // If that happens do not enqueue anything, for efficiency.
            toEnqueue = toEnqueue && !resultBuilder.IsComplete;

            if (toEnqueue)
            {
                var message = new MacroJobMessage(expansion,
                                                  this, queue,
                                                  promise.Id, resultBuilder,
                                                  cancellationToken);

                queue.Enqueue(message);
            }
            else
            {
                // When not enqueuing, we need to undo the registration
                // that would have been cleaned up by MacroJobMessage.
                UnregisterMacroJob(promise.Id, toCancel: false);
                resultBuilder = null;
            }

            return promise;
        }

        /// <summary>
        /// Create a push a "macro job" into a job queue which
        /// dynamically expands into many "micro jobs".
        /// </summary>
        /// <param name="owner">The owner of the queue. </param>
        /// <param name="priority">The desired priority class of the jobs. </param>
        /// <param name="promiseRetriever">
        /// Callback to the user's code to obtain the promise which 
        /// should receive the results from the job.  The desired 
        /// <see cref="Promise" /> object cannot be passed down directly, 
        /// because in the rare case that an existing job raced to cancel, 
        /// the promise object needs to be refreshed in a retry loop.
        /// </param>
        /// <param name="work">
        /// Describes the work to do in the scheduled job.
        /// Unlike for micro jobs, the work described in this argument
        /// is not "executed" directly; it only exists here so that
        /// it can be forwarded to <paramref name="promiseRetriever" />.
        /// </param>
        /// <param name="builderFactory">Provides the implementation
        /// of <see cref="IPromiseListBuilder" /> for gathering the
        /// promises generated from <paramref name="expansion" />,
        /// and storing them into <paramref name="promise" />.
        /// </param>
        /// <param name="expansion">
        /// Expands the macro job into the micro jobs once 
        /// it has been de-queued and ready to run.
        /// </param>
        /// <param name="cancellationToken">
        /// Used by the caller to request cancellation of the macro job
        /// and any micro jobs created from it.
        /// </param>
        public Promise PushMacroJob(IJobQueueOwner owner, 
                                    int priority,
                                    PromiseRetriever promiseRetriever,
                                    PromisedWork work,
                                    PromiseListBuilderFactory builderFactory,
                                    MacroJobExpansion expansion,
                                    CancellationToken cancellationToken)
        {
            return PushMacroJobCore(owner, priority, 
                                    promiseRetriever, work, 
                                    builderFactory, expansion,
                                    ownsCancellation: false,
                                    cancellationToken,
                                    out _);
        }

        /// <summary>
        /// Create a push a "macro job" into a job queue which
        /// dynamically expands into many "micro jobs",
        /// and create and track a cancellation token for it. 
        /// </summary>
        /// <param name="owner">The owner of the queue. </param>
        /// <param name="priority">The desired priority class of the jobs. </param>
        /// <param name="promiseRetriever">
        /// Callback to the user's code to obtain the promise which 
        /// should receive the results from the job.  The desired 
        /// <see cref="Promise" /> object cannot be passed down directly, 
        /// because in the rare case that an existing job raced to cancel, 
        /// the promise object needs to be refreshed in a retry loop.
        /// </param>
        /// <param name="work">
        /// Describes the work to do in the scheduled job.
        /// Unlike for micro jobs, the work described in this argument
        /// is not "executed" directly; it only exists here so that
        /// it can be forwarded to <paramref name="promiseRetriever" />.
        /// </param>
        /// <param name="builderFactory">Provides the implementation
        /// of <see cref="IPromiseListBuilder" /> for gathering the
        /// promises generated from <paramref name="expansion" />,
        /// and storing them into <paramref name="promise" />.
        /// </param>
        /// <param name="expansion">
        /// Expands the macro job into the micro jobs once 
        /// it has been de-queued and ready to run.
        /// </param>
        public Promise PushMacroJobAndOwnCancellation(IJobQueueOwner owner,
                                                      int priority,
                                                      PromiseRetriever promiseRetriever,
                                                      PromisedWork work,
                                                      PromiseListBuilderFactory builderFactory,
                                                      MacroJobExpansion expansion)
        {
            var cancellation = CancellationSourcePool.Rent();

            var promise = PushMacroJobCore(owner, priority,
                                           promiseRetriever, work,
                                           builderFactory, expansion,
                                           ownsCancellation: true,
                                           cancellation.Token,
                                           out IPromiseListBuilder? resultBuilder);

            if (resultBuilder is null)
            {
                // resultBuilder being null means no job message was enqueued,
                // and therefore nothing will call UnregisterCancellationUse.
                cancellation.Dispose();
            }
            else
            {
                RegisterCancellationUse(owner, promise.Id, ref cancellation);

                // Undo if the promise could have raced to complete and clean up
                if (resultBuilder.IsComplete)
                    UnregisterCancellationUse(promise.Id);
            }

            return promise;
        }

        #endregion
    }

    /// <summary>
    /// The message type put into the queues managed by
    /// <see cref="JobSchedulingSystem" /> that implements
    /// a "macro job".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A "macro job" expands into many "micro jobs" only when
    /// the macro job is de-queued.  This feature avoids 
    /// having to push hundreds and thousands of messages 
    /// (for the micro jobs) into the queue which makes it
    /// hard to visualize and manage.  Resources can also
    /// be conserved if the generator of the micro jobs
    /// is able to, internally, represent micro jobs more 
    /// efficiently than generic messages in the job
    /// scheduling queues.
    /// </para>
    /// <para>
    /// A user-supplied generator
    /// lists out the micro jobs as <see cref="PromisedWork" />
    /// descriptors, and this class transforms them into
    /// the messages that are put into the job queue,
    /// to implement job sharing and time accounting.
    /// </para>
    /// </remarks>
    internal class MacroJobMessage : IAsyncEnumerable<JobMessage>
    {
        private readonly MacroJobExpansion _expansion;

        private readonly JobSchedulingSystem _jobScheduling;
        private readonly ClientQueue _queue;
        private readonly PromiseId _promiseId;
        private readonly IPromiseListBuilder _resultBuilder;
        private readonly CancellationToken _jobCancelToken;

        /// <summary>
        /// Construct the macro job message.
        /// </summary>
        /// <param name="expansion">
        /// User-supplied generator that lists out
        /// the promise objects and work descriptions for
        /// the micro jobs.
        /// </param>
        /// <param name="jobScheduling">
        /// The job scheduling system that this message is for.
        /// This reference is needed to push micro jobs into
        /// the job queue.
        /// </param>
        /// <param name="queue">
        /// The job queue that micro jobs will be pushed into.
        /// </param>
        /// <param name="promiseId">
        /// The promise ID for the macro job, needed to unregister
        /// it from <paramref name="jobScheduling" /> when the macro
        /// job has finished expanding.
        /// </param>
        /// <param name="resultBuilder">
        /// The list of promises generated by <paramref name="expansion" />
        /// will be stored/passed onto here.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for the macro job.
        /// For efficiency, all the micro jobs expanded from
        /// this macro job will share the same cancellation source,
        /// and micro jobs cannot be cancelled independently
        /// of one another.
        /// </param>
        internal MacroJobMessage(MacroJobExpansion expansion,
                                 JobSchedulingSystem jobScheduling,
                                 ClientQueue queue,
                                 PromiseId promiseId,
                                 IPromiseListBuilder resultBuilder,
                                 CancellationToken cancellationToken)
        {
            _expansion = expansion;
            _jobScheduling = jobScheduling;
            _queue = queue;
            _promiseId = promiseId;
            _resultBuilder = resultBuilder;
            _jobCancelToken = cancellationToken;
        }

        /// <inheritdoc cref="IAsyncEnumerable{T}.GetAsyncEnumerator" />
        public async IAsyncEnumerator<JobMessage>
            GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            int count = 0;
            Exception? exception = null;

            //
            // We cannot write the following loop as a straightforward
            // "await foreach" with "yield return" inside, because
            // we need to catch exceptions.  We must control the
            // enumerator manually.
            //

            IAsyncEnumerator<(PromiseRetriever, PromisedWork)>? enumerator = null;
            try
            {
                // Do not do anything if another producer has already completed.
                if (_resultBuilder.IsComplete)
                    yield break;

                enumerator = _expansion.GetAsyncEnumerator(
                    cancellationToken.CanBeCanceled ? cancellationToken
                                                    : _jobCancelToken);
            }
            catch (Exception e)
            {
                exception = e;
            }

            if (enumerator is not null)
            {
                while (true)
                {
                    JobMessage? message;

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _jobCancelToken.ThrowIfCancellationRequested();

                        // Stop generating job messages if another producer
                        // has completely done so, or there are no more micro jobs.
                        if (_resultBuilder.IsComplete ||
                            !await enumerator.MoveNextAsync().ConfigureAwait(false))
                            break;

                        cancellationToken.ThrowIfCancellationRequested();
                        _jobCancelToken.ThrowIfCancellationRequested();

                        var (promiseRetriever, input) = enumerator.Current;

                        message = _jobScheduling.RegisterJobMessage(
                                        _queue,
                                        promiseRetriever,
                                        input,
                                        ownsCancellation: false,
                                        _jobCancelToken,
                                        out var promise);

                        // Add the new member to the result sequence.
                        _resultBuilder.SetMember(count, promise);
                        count++;

                        // Do not schedule work if promise is already complete
                        if (message is null)
                            continue;
                    }
                    catch (Exception e)
                    {
                        exception = e;
                        break;
                    }

                    yield return message.GetValueOrDefault();
                }

                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    exception ??= e;
                }
            }

            if (exception is OperationCanceledException cancelException)
            {
                if (cancelException.CancellationToken == _jobCancelToken)
                {
                    // Complete _resultBuilder with the cancellation exception
                    // only if this producer is the last to cancel.
                    if (_jobScheduling.UnregisterMacroJob(_promiseId, toCancel: true))
                        _resultBuilder.TryComplete(count, exception);

                    yield break;
                }
                else if (cancelException.CancellationToken == cancellationToken)
                {
                    // Do not terminate _resultBuilder when cancelling locally
                    // on this enumerator.
                    yield break;
                }
            }

            // When this producers finishes successfully, or the
            // exception is not transient, complete _resultBuilder.
            //
            // If this producer is the first producer then unregister
            // the promise as well.  UnregisterMacroPromise could be
            // called unconditionally here and it would do nothing
            // if this is not the first producer.
            if (_resultBuilder.TryComplete(count, exception))
                _jobScheduling.UnregisterMacroJob(_promiseId, toCancel: false);
        }
    }

    /// <summary>
    /// Function type for <see cref="JobSchedulingSystem" />
    /// to instantiate the output object for a macro job.
    /// </summary>
    /// <param name="promise">
    /// The promise that the output will be attached to.
    /// </param>
    /// <returns>
    /// Implementation of <see cref="IPromiseListBuilder" />
    /// that will be set to <see cref="Promise.ResultOutput" />
    /// for the macro job.
    /// </returns>
    public delegate IPromiseListBuilder PromiseListBuilderFactory(Promise promise);

    /// <summary>
    /// Invoked by <see cref="JobSchedulingSystem" /> to obtain
    /// a promise object that will receive the output from a job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unfortunately, the instance of <see cref="Promise" />
    /// cannot be passed directly to the methods of 
    /// <see cref="JobSchedulingSystem" /> because of a fundamental
    /// race condition in scheduling jobs that can be shared.  Any 
    /// promise that gets passed in may receive a cancellation
    /// result (i.e. <see cref="OperationCanceledException"/> 
    /// represented as <see cref="PromiseData" />) if the preceding
    /// clients of the shared job all cancelled, before a new incoming
    /// client manages to attach itself to the same job.
    /// </para>
    /// <para>
    /// The race condition can only be resolved inside the implementation
    /// of <see cref="JobSchedulingSystem" />, not by its caller.
    /// Since promise outputs are immutable, when the race happens, 
    /// <see cref="JobSchedulingSystem" /> must request a fresh
    /// <see cref="Promise" /> object to receive the results of
    /// the restarted job.  It does so using this delegate.
    /// </para>
    /// <para>
    /// Under the normal situations when the cancellation race does
    /// not occur, the caller may place the promise it wishes
    /// to receive the result into <see cref="PromisedWork.Promise" />,
    /// and this function can return it back.
    /// </para>
    /// </remarks>
    /// <param name="work">
    /// The description of the work that <see cref="JobSchedulingSystem" />
    /// has been asked to schedule.  This argument is a copy of 
    /// what the caller of <see cref="JobSchedulingSystem" /> has
    /// passed in, except that the property <see cref="PromisedWork.Promise" />
    /// will be set to null if a fresh promise object is needed.
    /// </param>
    /// <returns>
    /// The promise object that is to receive the result of the work,
    /// via <see cref="Promise.TryAwaitAndPostResult" />.
    /// </returns>
    public delegate Promise PromiseRetriever(PromisedWork work);
}
