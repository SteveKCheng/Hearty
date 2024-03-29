﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Scheduling;
using Hearty.Common;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Hearty.Server;

using JobMessage = ILaunchableJob<PromisedWork, PromiseData>;
using MacroJobExpansion = IAsyncEnumerable<(PromiseRetriever, PromisedWork)>;

/// <summary>
/// Schedules jobs, on behalf of clients, to fulfill promises.
/// </summary>
/// <remarks>
/// The systems for managing job queueing, distributing jobs to 
/// (remote) workers, and holding the results as promises are 
/// implemented orthogonally.  This class integrates them together 
/// into a coherent service for applications to schedule execution 
/// of promises.
/// </remarks>
public class JobsManager : IRemoteJobCancellation
{
    /// <summary>
    /// Logs important operations performed by this instance.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Translates exceptions from executing a job into promise output.
    /// </summary>
    private readonly PromiseExceptionTranslator _exceptionTranslator;

    /// <summary>
    /// Accepts client jobs after they have been registered and de-duplicated
    /// by this class.
    /// </summary>
    private readonly IJobQueueSystem _jobQueues;

    /// <summary>
    /// Metrics on the jobs managed by this instance are sent to this instance.
    /// </summary>
    public JobServerMetrics Metrics { get; }

    /// <summary>
    /// Delegate that is invoked whenever a job completes, whether successful or not.
    /// </summary>
    /// <remarks>
    /// This delegate is called at most once per promise, even if the same promise
    /// is submitted multiple times for job processing.
    /// </remarks>
    public EventHandler<Promise>? OnJobComplete;

    /// <summary>
    /// Prepare the system to schedule jobs and assign them to workers.
    /// </summary>
    /// <param name="logger">
    /// Receives log messages for significant events in the job scheduling
    /// system.
    /// </param>
    /// <param name="metrics">
    /// Used to report metrics.
    /// </param>
    /// <param name="exceptionTranslator">
    /// Translates .NET exceptions when they occur as a result of 
    /// executing the work for promises, when <see cref="PromisedWork.ExceptionTranslator" />
    /// is not able to do so.
    /// </param>
    /// <param name="jobQueues">
    /// Accepts client jobs after they have been registered and de-duplicated
    /// by this class.
    /// </param>        
    public JobsManager(ILogger<JobsManager> logger,
                       PromiseExceptionTranslator exceptionTranslator,
                       IJobQueueSystem jobQueues,
                       JobServerMetrics? metrics = null)
    {
        _logger = logger;
        _exceptionTranslator = exceptionTranslator;
        _jobQueues = jobQueues;
        Metrics = metrics ?? JobServerMetrics.Default;

        _unregisterClientJobAction = (future, _, clientToken) =>
                this.UnregisterClientRequest(future.Input.Promise!.Id, 
                                             clientToken);
    }

    /// <summary>
    /// Expiry queue used to update counters of elapsed time for fair
    /// job scheduling.
    /// </summary>
    private readonly SimpleExpiryQueue _timingQueue
        = new SimpleExpiryQueue(1000, 50);

    #region Registration of client requests

    /// <summary>
    /// Track outstanding requests to be able to de-duplicate them and
    /// cancel them on behalf of (remote) clients.
    /// </summary>
    private readonly Dictionary<(PromiseId, CancellationToken), IJobCancellation>
        _clientRequests = new();

    /// <summary>
    /// Unregister an outstanding request of a promise from a client.
    /// </summary>
    /// <remarks>
    /// This method should be called once the job (for the client)
    /// has finished processing, successful or not.
    /// </remarks>
    internal void UnregisterClientRequest(PromiseId promiseId, CancellationToken clientToken)
    {
        lock (_clientRequests)
            _clientRequests.Remove((promiseId, clientToken));
    }

    /// <summary>
    /// Register a request of a promise from a client unless
    /// it already exists.
    /// </summary>
    /// <returns>
    /// True if the request is successfully added; false
    /// if it already exists.
    /// </returns>
    internal bool TryRegisterClientRequest(PromiseId promiseId, 
                                           CancellationToken clientToken,
                                           IJobCancellation job)
    {
        lock (_clientRequests)
            return _clientRequests.TryAdd((promiseId, clientToken), job);
    }

    /// <inheritdoc cref="IRemoteJobCancellation.TryCancelJobForClient" />
    public bool TryCancelJobForClient(JobQueueKey queueKey, PromiseId promiseId)
    {
        var queue = _jobQueues.TryGetJobQueue(queueKey);
        if (queue is null)
            return false;

        var clientToken = queue.CancellationToken;

        IJobCancellation? target;
        lock (_clientRequests)
            _clientRequests.Remove((promiseId, clientToken), out target);

        return target?.CancelForClient(clientToken, background: true) ?? false;
    }

    /// <inheritdoc cref="IRemoteJobCancellation.TryKillJob" />
    public bool TryKillJob(PromiseId promiseId)
    {
        SharedFuture<PromisedWork, PromiseData>? future;
        lock (_microPromises)
            _microPromises.Remove(promiseId, out future);

        if (future is not null)
        {
            future.Kill(background: true);
            return true;
        }

        MacroJob? macroJob;
        lock (_macroPromises)
            _macroPromises.Remove(promiseId, out macroJob);

        if (macroJob is not null)
        {
            macroJob.Kill(background: true);
            return true;
        }

        return false;
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
                                SharedFuture<PromisedWork, PromiseData>>
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
        lock (_microPromises)
        {
            bool isRemoved = _microPromises.Remove(promiseId, out var removedEntry);
        }
    }

    /// <summary>
    /// Action to run when an (enqueued) job completes: clears
    /// the job's registration and invokes <see cref="OnJobComplete" />.
    /// </summary>
    private void InvokeOnJobComplete(Promise promise)
    {
        RemoveCachedFuture(promise.Id);
        OnJobComplete?.Invoke(this, promise);
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
        if (EnsureNonNullPromise(newPromise).Id == oldId)
        {
            throw new InvalidOperationException(
                "A promise with a new ID is not being supplied from PromiseRetrieval " +
                "when the preceding ID cannot be used any longer. ");
        }
    }

    /// <summary>
    /// If the passed in promise object is null, 
    /// throw an exception saying that <see cref="PromiseRetriever" />
    /// returned null, to help in diagnosing errors in using the API.
    /// </summary>
    private static Promise EnsureNonNullPromise(Promise promise)
    {
        if (promise is null)
        {
            throw new ArgumentNullException(paramName: null,
                                            message: "PromiseRetriever returned null. ");
        }

        return promise;
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
    /// <param name="registerClient">
    /// Whether the job should be registered in the table
    /// of client requests so it can be cancelled 
    /// by <see cref="IJobCancellation" />.  If true,
    /// <paramref name="cancellationToken" /> should be
    /// the client's token.
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
    /// <see cref="Promise.Id" /> of <paramref name="promise" />.
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
                           bool registerClient,
                           CancellationToken cancellationToken,
                           out Promise promise)
    {
        Task<PromiseData>? outputTask;
        JobMessage? message;

        promise = EnsureNonNullPromise(promiseRetriever.Invoke(work));

        // Retry loop for concurrently cancelled promises
        while ((message = TryRegisterJobMessage(account, promise, work, 
                                                registerClient,
                                                cancellationToken,
                                                out outputTask)) is null)
        {
            // Stop the retry loop when the result is permanent,
            // i.e. not transient.
            //
            // There may be an interval where TryShareJob sees the
            // cancellation having been triggered, but the promise
            // has not yet completed because it takes time for 
            // the result to get propagated as a cancellation
            // exception.  Fortunately, cancellation should be the
            // only case where the promise would come out as 
            // uncompleted, which makes it easy to check for.
            if (promise.HasOutput && !promise.HasTransientOutput)
                return null;

            // We are careful to not invoke this callback inside the lock
            // on _microPromises, taken by TryRegisterJobMessage.
            var oldPromiseId = promise.Id;
            promise = promiseRetriever.Invoke(work.ReplacePromise(null));
            VerifyPromiseIsDifferent(promise, oldPromiseId);
        }

        // Only attach outputTask to the promise if this is a new job
        if (outputTask is not null)
        {
            bool awaiting = promise.TryAwaitAndPostResult(
                                new ValueTask<PromiseData>(outputTask),
                                _exceptionTranslator,
                                p => InvokeOnJobComplete(p));

            // awaiting == false should not happen unless some code outside
            // of this class is posting to the promise.  In that case, recover
            // by reversing our registration.  Since the job message has not
            // been queued yet, there should be no harm.
            if (!awaiting)
            {
                // FIXME client should be cancelled too
                RemoveCachedFuture(promise.Id);
                return null;
            }
        }
        else
        {
            // Check again if the promise completed, and if so,
            // the caller should not enqueue the message.
            if (promise.HasOutput)
                return null;
        }

        return message;
    }

    /// <summary>
    /// Callback to unregister a client of a micro job, when
    /// client request tracking is enabled.
    /// </summary>
    private readonly SharedFuture<PromisedWork, PromiseData>.ClientFinishingAction
        _unregisterClientJobAction;

    /// <summary>
    /// One iteration in the retry loop of <see cref="RegisterJobMessage" />.
    /// </summary>
    private JobMessage? TryRegisterJobMessage(ISchedulingAccount account,
                                              Promise promise,
                                              in PromisedWork work,
                                              bool registerClient,
                                              CancellationToken cancellationToken,
                                              out Task<PromiseData>? outputTask)
    {
        SharedFuture<PromisedWork, PromiseData> future;
        outputTask = null;

        // Do not register any job if the promise is already complete
        if (promise.HasOutput)
            return null;

        var promiseId = promise.Id;

        var onClientFinish = registerClient ? _unregisterClientJobAction
                                            : null;

        lock (_microPromises)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                _microPromises, promiseId, out bool exists);

            // Attach account to existing job for same promise ID if it exists
            // and has not been cancelled.
            if (exists)
            {
                future = entry!;

                if (registerClient && 
                    !TryRegisterClientRequest(promiseId, cancellationToken, future))
                {
                    return null;
                }

                if (!future.TryShareJob(account, cancellationToken, onClientFinish))
                {
                    // Back out if sharing the job failed
                    if (registerClient)
                        UnregisterClientRequest(promiseId, cancellationToken);

                    return null;
                }
            }
            else
            {
                // Usual case: completely new job
                try
                {
                    future = new SharedFuture<PromisedWork, PromiseData>(
                            work.ReplacePromise(promise),
                            work.InitialWait,
                            account,
                            cancellationToken,
                            onClientFinish,
                            _timingQueue);
                }
                catch
                {
                    _microPromises.Remove(promiseId);
                    throw;
                }

                if (registerClient &&
                    !TryRegisterClientRequest(promiseId, cancellationToken, future))
                {
                    // Probably should not happen
                    _microPromises.Remove(promiseId);
                    return null;
                }

                outputTask = future.OutputTask;
                entry = future;
            }
        }

        return future;
    }

    /// <summary>
    /// Create and push a job to complete a promise.
    /// </summary>
    /// <param name="queueKey">
    /// Selects the queue to push the job into.
    /// </param>
    /// <param name="ownerPrincipal">
    /// "Principal" object describing the owner of the queue
    /// if the queue is to be created.  Ignored if the queue
    /// already exists.
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
    /// <param name="registerClient">
    /// If true, the client is registered for remote cancellation,
    /// and <paramref name="cancellationToken" /> is ignored.
    /// </param>
    /// <param name="cancellationToken">
    /// Used by the caller to request cancellation of the job.
    /// </param>
    public Promise PushJob(JobQueueKey queueKey,
                           ClaimsPrincipal? ownerPrincipal,
                           PromiseRetriever promiseRetriever,
                           in PromisedWork work,
                           bool registerClient,
                           CancellationToken cancellationToken = default)
    {
        var queue = _jobQueues.GetOrAddJobQueue(queueKey, ownerPrincipal);

        if (registerClient)
            cancellationToken = queue.CancellationToken;

        var message = RegisterJobMessage(
                        queue.SchedulingAccount, promiseRetriever, work,
                        registerClient,
                        cancellationToken,
                        out var promise);

        if (message is not null)
            queue.Enqueue(message);

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
    private readonly Dictionary<PromiseId, MacroJob>
        _macroPromises = new();

    /// <summary>
    /// Unregister the macro job/promise when it has finished expanding.
    /// </summary>
    /// <param name="promiseId">The promise ID for the macro job
    /// which registered it.
    /// </param>
    /// <returns>
    /// Whether the registration entry existed and has been removed.
    /// </returns>
    internal bool UnregisterMacroJob(PromiseId promiseId)
    {
        lock (_macroPromises)
        {
            bool isRemoved = _macroPromises.Remove(promiseId);
            return isRemoved;
        }
    }

    /// <summary>
    /// Re-factored code for pushing a macro job with
    /// or without its own cancellation source.
    /// </summary>
    private MacroJobMessage? RegisterMacroJob(
        ClientJobQueue queue,
        PromiseRetriever promiseRetriever,
        in PromisedWork work,
        PromiseListBuilderFactory builderFactory,
        MacroJobExpansion expansion,
        CancellationToken cancellationToken,
        out Promise promise)
    {
        bool isNewJob;

        promise = EnsureNonNullPromise(promiseRetriever.Invoke(work));

        MacroJobMessage? message;

        // Retry loop for concurrently cancelled promises
        while ((message = TryRegisterMacroJob(queue,
                                              work.ReplacePromise(promise),
                                              promise, 
                                              builderFactory, 
                                              expansion,
                                              cancellationToken,
                                              out isNewJob)) is null && isNewJob)
        {
            // Like in RegisterJobMessage, do not take locks on
            // _macroPromises while invoking this callback function.
            var oldPromiseId = promise.Id;
            promise = promiseRetriever.Invoke(work.ReplacePromise(null));
            VerifyPromiseIsDifferent(promise, oldPromiseId);
        }

        // A non-null message means there may be something to enqueue,
        // because it has not finished yet.
        if (message is not null && isNewJob)
        {
            // Need to set the result builder into the promise for a new job
            bool shouldEnqueue = promise.TryAwaitAndPostResult(
                                ValueTask.FromResult(
                                    message.Source.ResultBuilder.Output),
                                _exceptionTranslator);
            if (!shouldEnqueue)
            {
                message.Dispose();
                message = null;
            }
        }

        return message;
    }

    /// <summary>
    /// One iteration in the retry loop of <see cref="RegisterMacroJob" />.
    /// </summary>
    private MacroJobMessage? TryRegisterMacroJob(
                                ClientJobQueue queue,
                                in PromisedWork work,
                                Promise promise,
                                PromiseListBuilderFactory builderFactory,
                                MacroJobExpansion expansion,
                                CancellationToken cancellationToken,
                                out bool isNewJob)
    {
        // Can re-use existing promise without creating a new job
        // when its output is completed without a transient result.
        static bool HasGoodCompletedOutput(PromiseData? existingOutput)
            => existingOutput is not null &&
               existingOutput.IsComplete &&
               !existingOutput.IsTransient;

        if (HasGoodCompletedOutput(promise.ResultOutput))
        {
            isNewJob = false;
            return null;
        }

        // This code is separated into a local function to avoid
        // "goto" for re-doing operations due to concurrency conflict.
        static MacroJobMessage? ProcessExistingEntry(in PromisedWork work,
                                                     MacroJob entry,
                                                     PromiseData? existingOutput,
                                                     ClientJobQueue queue,
                                                     CancellationToken cancellationToken,
                                                     out bool isNewJob)
        {
            var message = new MacroJobMessage(work, entry, queue, cancellationToken);
            if (message.IsValid)
            {
                isNewJob = false;
                return message;
            }
            else
            {
                // Must create a new job when existing promise cannot be re-used
                isNewJob = !HasGoodCompletedOutput(existingOutput);
                return null;
            }
        }

        var promiseId = promise.Id;

        lock (_macroPromises)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(
                                _macroPromises, promiseId);

            // Promise is already registered.
            if (!Unsafe.IsNullRef(ref entry))
            {
                return ProcessExistingEntry(work,
                                            entry!,
                                            promise.ResultOutput,
                                            queue, 
                                            cancellationToken, 
                                            out isNewJob);
            }
        }

        // The usual case: the promise is not already registered.
        //
        // Create a result builder for a new job.
        // Temporarily release the lock to invoke the callback.
        var resultBuilder = builderFactory.Invoke(promise)
                            ?? throw new ArgumentNullException(
                                paramName: null,
                                message: "PromiseListBuilderFactory returned null. ");

        lock (_macroPromises)
        {
            // ref var entry may have been invalidated by concurrent
            // calls to this method.
            ref var newEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                _macroPromises, promiseId, out bool exists);

            // A concurrent caller has already added the entry.
            // Restart as if it had already existed earlier,
            // and discard resultBuilder.
            if (exists)
            {
                return ProcessExistingEntry(work, 
                                            newEntry!,
                                            promise.ResultOutput,
                                            queue,
                                            cancellationToken,
                                            out isNewJob);
            }

            // Populate the new entry.
            var macroJob = new MacroJob(this, resultBuilder, promiseId, expansion);
            var message = new MacroJobMessage(work, macroJob, queue, cancellationToken);
            newEntry = macroJob;
            isNewJob = true;
            return message;
        }
    }

    /// <summary>
    /// Create a push a "macro job" into a job queue which
    /// dynamically expands into many "micro jobs".
    /// </summary>
    /// <param name="queueKey">
    /// Selects the queue to push the jobs into.
    /// </param>
    /// <param name="ownerPrincipal">
    /// "Principal" object describing the owner of the queue
    /// if the queue is to be created.  Ignored if the queue
    /// already exists.
    /// </param>
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
    /// and storing them into the created promise.
    /// </param>
    /// <param name="expansion">
    /// Expands the macro job into the micro jobs once 
    /// it has been de-queued and ready to run.
    /// </param>
    /// <param name="registerClient">
    /// If true, the client is registered for remote cancellation,
    /// and <paramref name="cancellationToken" /> is ignored.
    /// </param>
    /// <param name="cancellationToken">
    /// Used by the caller to request cancellation of the macro job
    /// and any micro jobs created from it.
    /// </param>
    public Promise PushMacroJob(JobQueueKey queueKey,
                                ClaimsPrincipal? ownerPrincipal,
                                PromiseRetriever promiseRetriever,
                                in PromisedWork work,
                                PromiseListBuilderFactory builderFactory,
                                MacroJobExpansion expansion,
                                bool registerClient,
                                CancellationToken cancellationToken = default)
    {
        var queue = _jobQueues.GetOrAddJobQueue(queueKey, ownerPrincipal);

        if (registerClient)
            cancellationToken = queue.CancellationToken;

        var message = RegisterMacroJob(queue,
                                       promiseRetriever, work,
                                       builderFactory, expansion,
                                       cancellationToken,
                                       out var promise);

        if (message is not null)
        {
            Exception? exception = null;

            try
            {
                if (!registerClient || message.TryTrackClientRequest())
                {
                    queue.Enqueue(message);
                    message = null;
                    Metrics.MacroJobsQueued.Add(1);
                }
            }
            catch (Exception e)
            {
                exception = e;
            }
            finally
            {
                message?.DisposeWithException(exception);
            }
        }

        return promise;
    }

    /// <summary>
    /// Create a push a "macro job" into a job queue which
    /// dynamically expands into many "micro jobs".
    /// </summary>
    /// <param name="queueKey">
    /// Selects the queue to push the jobs into.
    /// </param>
    /// <param name="ownerPrincipal">
    /// "Principal" object describing the owner of the queue
    /// if the queue is to be created.  Ignored if the queue
    /// already exists.
    /// </param>
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
    /// and storing them into the created promise.
    /// </param>
    /// <param name="expansion">
    /// Expands the macro job into the micro jobs once 
    /// it has been de-queued and ready to run.
    /// </param>
    /// <param name="registerClient">
    /// If true, the client is registered for remote cancellation,
    /// and <paramref name="cancellationToken" /> is ignored.
    /// </param>
    /// <param name="cancellationToken">
    /// Used by the caller to request cancellation of the macro job
    /// and any micro jobs created from it.
    /// </param>
    public Promise PushMacroJob(JobQueueKey queueKey,
                                ClaimsPrincipal? ownerPrincipal,
                                PromiseRetriever promiseRetriever,
                                PromisedWork work,
                                PromiseListBuilderFactory builderFactory,
                                IEnumerable<(PromiseRetriever, PromisedWork)> expansion,
                                bool registerClient,
                                CancellationToken cancellationToken = default)
        => PushMacroJob(queueKey,
                        ownerPrincipal,
                        promiseRetriever,
                        work,
                        builderFactory,
                        new AsyncEnumerableAdaptor<(PromiseRetriever, PromisedWork)>(expansion),
                        registerClient,
                        cancellationToken);

    #endregion
}

/// <summary>
/// Function type for <see cref="JobsManager" />
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
/// Invoked by <see cref="JobsManager" /> to obtain
/// a promise object that will receive the output from a job.
/// </summary>
/// <remarks>
/// <para>
/// Unfortunately, the instance of <see cref="Promise" />
/// cannot be passed directly to the methods of 
/// <see cref="JobsManager" /> because of a fundamental
/// race condition in scheduling jobs that can be shared.  Any 
/// promise that gets passed in may receive a cancellation
/// result (i.e. <see cref="OperationCanceledException"/> 
/// represented as <see cref="PromiseData" />) if the preceding
/// clients of the shared job all cancelled, before a new incoming
/// client manages to attach itself to the same job.
/// </para>
/// <para>
/// The race condition can only be resolved inside the implementation
/// of <see cref="JobsManager" />, not by its caller.
/// Since promise outputs are immutable, when the race happens, 
/// <see cref="JobsManager" /> must request a fresh
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
/// The description of the work that <see cref="JobsManager" />
/// has been asked to schedule.  This argument is a copy of 
/// what the caller of <see cref="JobsManager" /> has
/// passed in, except that the property <see cref="PromisedWork.Promise" />
/// will be set to null if a fresh promise object is needed.
/// </param>
/// <returns>
/// The promise object that is to receive the result of the work,
/// via <see cref="Promise.TryAwaitAndPostResult" />.
/// </returns>
public delegate Promise PromiseRetriever(PromisedWork work);
