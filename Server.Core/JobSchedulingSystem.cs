using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JobBank.Scheduling;
using JobBank.Utilities;
using Microsoft.Extensions.Logging;

namespace JobBank.Server
{
    using JobMessage = ScheduledJob<PromiseJob, PromiseData>;
    using ClientQueue = SchedulingQueue<ScheduledJob<PromiseJob, PromiseData>>;
    using OwnerCancellation = KeyValuePair<IJobQueueOwner, CancellationSourcePool.Use>;

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
        public WorkerDistribution<PromiseJob, PromiseData>
            WorkerDistribution { get; }

        /// <summary>
        /// Cached "future" objects for jobs that have been submitted to at
        /// least one job queue.
        /// </summary>
        private readonly Dictionary<PromiseId, 
                                    SharedFuture<PromiseJob, PromiseData>>
            _futures = new();

        /// <summary>
        /// Track <see cref="CancellationTokenSource" /> that are
        /// registered together with the jobs that are not supplied
        /// with external cancellation tokens.
        /// </summary>
        private readonly Dictionary<PromiseId,
                                    SmallList<OwnerCancellation>>
            _cancellations = new();

        private readonly ILogger _logger;

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
                                   WorkerDistribution<PromiseJob, PromiseData> workerDistribution,
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

        /// <summary>
        /// Get a scheduled job entry to push into a client's queue;
        /// the future object may be shared if the same job
        /// has been pushed before (for another client).
        /// </summary>
        /// <param name="account">Scheduling account corresponding to
        /// the client's queue. </param>
        /// <param name="request">Input for the job to execute. 
        /// This input is also used to de-duplicate jobs.
        /// </param>
        /// <param name="charge"></param>
        /// <returns></returns>
        private JobMessage
            GetJobMessage(ISchedulingAccount account, 
                          PromiseId promiseId,
                          PromiseJob request, 
                          int charge,
                          out Task<PromiseData> outputTask,
                          CancellationToken cancellationToken)
        {
            JobMessage message;

            lock (_futures)
            {
                // Attach account to existing job for same promise ID if it exists
                if (_futures.TryGetValue(promiseId, out var future) && !future.IsCancelled)
                {
                    message = future.CreateJob(account, cancellationToken);
                    outputTask = future.OutputTask;

                    // Check again because cancellation can race with registering
                    // a new client
                    if (!future.IsCancelled)
                        return message;
                }

                // Usual case: completely new job
                message = SharedFuture<PromiseJob, PromiseData>.CreateJob(
                        request,
                        charge,
                        account,
                        cancellationToken,
                        _timingQueue,
                        out future);

                outputTask = future.OutputTask;
                _futures[promiseId] = future;
            }

            return message;
        }

        /// <summary>
        /// Remove the entry mapping a promise ID to a scheduled job.
        /// </summary>
        /// <remarks>
        /// This method is to be called once the job is finished
        /// to avoid unbounded growth in the mapping table.
        /// </remarks>
        private void RemoveCachedFuture(PromiseId promiseId)
        {
            lock (_futures)
            {
                _futures.Remove(promiseId);
            }

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
        /// Create and push a job to complete a promise.
        /// </summary>
        /// <param name="owner">
        /// Owner of the queue.
        /// </param>
        /// <param name="priority">
        /// The desired priority that the job should be enqueued into.  It is expressed
        /// using the same integer key as used by <see cref="PriorityClasses" />.
        /// </param>
        /// <param name="charge"></param>
        /// <param name="promise">
        /// A promise which may be newly created or already existing.
        /// If newly created, the asynchronous task for the job
        /// is posted into it.  Otherwise the existing asynchronous task
        /// is consumed, and <paramref name="job" /> is ignored.
        /// </param>
        /// <param name="job">
        /// Produces the result output of the promise.
        /// </param>
        /// <param name="cancellationToken">
        /// Used by the caller to request cancellation of the job.
        /// </param>
        public void PushJob(IJobQueueOwner owner,
                            int priority,
                            int charge,
                            Promise promise,
                            PromiseJob job,
                            CancellationToken cancellationToken)
        {
            // Enqueue nothing if promise is already completed
            if (promise.IsCompleted)
                return;

            var queue = PriorityClasses[priority].GetOrAdd(owner);

            var message = RegisterJob(queue, charge, 
                                      promise, job, cancellationToken);

            queue.Enqueue(message);
        }

        internal JobMessage RegisterJob(
            ClientQueue queue,
            int charge,
            Promise promise,
            PromiseJob job,
            CancellationToken cancellationToken)
        {
            var message = GetJobMessage(queue,
                                        promise.Id,
                                        job,
                                        charge,
                                        out var outputTask,
                                        cancellationToken);

            promise.TryAwaitAndPostResult(new ValueTask<PromiseData>(outputTask),
                                          _exceptionTranslator,
                                          p => RemoveCachedFuture(p.Id));

            return message;
        }

        private CancellationToken CreateCancellationToken(IJobQueueOwner owner, 
                                                          PromiseId promiseId)
        {
            var use = CancellationSourcePool.Rent();
            var item = new OwnerCancellation(owner, use);

            lock (_cancellations)
            {
                if (_cancellations.TryGetValue(promiseId, out var oldList))
                {
                    var a = new OwnerCancellation[oldList.Count + 1];
                    for (int i = 0; i < oldList.Count; ++i)
                        a[i] = oldList[i];
                    a[oldList.Count] = item;
                    _cancellations[promiseId] = new SmallList<OwnerCancellation>(a);
                }
                else
                {
                    _cancellations.Add(promiseId, new SmallList<OwnerCancellation>(item));
                }
            }

            return use.Token;
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
        /// <param name="charge"></param>
        /// <param name="promise">
        /// A promise which may be newly created or already existing.
        /// If newly created, the asynchronous task for the job
        /// is posted into it.  Otherwise the existing asynchronous task
        /// is consumed, and <paramref name="job" /> is ignored.
        /// </param>
        /// <param name="job">
        /// Produces the result output of the promise.
        /// </param>
        public void PushJobAndSourceCancellation(IJobQueueOwner owner,
                                                 int priority,
                                                 int charge,
                                                 Promise promise, 
                                                 PromiseJob job)
        {
            var cancellationToken = CreateCancellationToken(owner, promise.Id);
            PushJob(owner,
                    priority,
                    charge,
                    promise,
                    job,
                    cancellationToken);
        }

        /// <summary>
        /// Cancel a job that was pushed with
        /// <see cref="PushJobAndSourceCancellation" />.
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
    /// lists out the micro jobs as <see cref="PromiseJob" />
    /// descriptors, and this class transforms them into
    /// the messages that are put into the job queue,
    /// to implement job sharing and time accounting.
    /// </para>
    /// </remarks>
    internal class MacroJobMessage : IAsyncEnumerable<JobMessage>
    {
        private readonly IAsyncEnumerable<(Promise, PromiseJob)> _generator;

        private readonly JobSchedulingSystem _jobScheduling;
        private readonly ClientQueue _queue;
        private readonly CancellationToken _cancellationToken;

        /// <summary>
        /// Construct the macro job message.
        /// </summary>
        /// <param name="jobScheduling">
        /// The job scheduling system that this message is for.
        /// This reference is needed to push micro jobs into
        /// the job queue.
        /// </param>
        /// <param name="queue">
        /// The job queue that micro jobs will be pushed into.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for the macro job.
        /// For efficiency, all the micro jobs expanded from
        /// this macro job will share the same cancellation source,
        /// and micro jobs cannot be cancelled independently
        /// of one another.
        /// </param>
        /// <param name="generator">
        /// User-supplied generator that lists out
        /// the promise objects and work descriptions for
        /// the micro jobs.
        /// </param>
        internal MacroJobMessage(IAsyncEnumerable<(Promise, PromiseJob)> generator,
                                 JobSchedulingSystem jobScheduling,
                                 ClientQueue queue,
                                 CancellationToken cancellationToken)
        {
            _generator = generator;
            _jobScheduling = jobScheduling;
            _queue = queue;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Whether the iteration should stop immediately
        /// because the job was cancelled.
        /// </summary>
        private bool ShouldStop
            => _cancellationToken.IsCancellationRequested;

        /// <inheritdoc cref="IAsyncEnumerable{T}.GetAsyncEnumerator" />
        public async IAsyncEnumerator<JobMessage>
            GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (ShouldStop)
                yield break;

            await foreach (var (promise, input) in _generator)
            {
                if (ShouldStop)
                    yield break;

                var message = _jobScheduling.RegisterJob(
                                    _queue,
                                    0,
                                    promise,
                                    input,
                                    _cancellationToken);

                yield return message;
            }
        }
    }
}
