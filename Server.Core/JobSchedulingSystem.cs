using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JobBank.Scheduling;
using Microsoft.Extensions.Logging;

namespace JobBank.Server
{
    using JobMessage = ScheduledJob<PromiseJobFunction, PromiseData>;
    using ClientQueue = SchedulingQueue<ScheduledJob<PromiseJobFunction, PromiseData>>;

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
        public WorkerDistribution<PromiseJobFunction, PromiseData>
            WorkerDistribution { get; }

        /// <summary>
        /// Cached "future" objects for jobs that have been submitted to at
        /// least one job queue.
        /// </summary>
        private readonly Dictionary<PromiseId, 
                                    SharedFuture<PromiseJobFunction, PromiseData>>
            _futures = new();

        private class DummyWorker : IJobWorker<PromiseJobFunction, PromiseData>
        {
            public string Name => "DummyWorker";

            public async ValueTask<PromiseData> ExecuteJobAsync(uint executionId, 
                                                                IRunningJob<PromiseJobFunction> runningJob,
                                                                CancellationToken cancellationToken)
            {
                _logger.LogInformation("Starting job for execution ID {executionId}", executionId);

                var output = await runningJob.Input.InvokeAsync(runningJob, cancellationToken)
                                                   .ConfigureAwait(false);

                _logger.LogInformation("Completing job for execution ID {executionId}", executionId);
                return output;
            }

            public void AbandonJob(uint executionId)
            {
            }

            public bool IsAlive => true;


            public DummyWorker(ILogger logger)
            {
                _logger = logger;
            }

            private readonly ILogger _logger;

            public event EventHandler<WorkerEventArgs>? OnEvent;
        }

        private readonly ILogger _logger;

        /// <summary>
        /// Prepare the system to schedule jobs and assign them to workers.
        /// </summary>
        /// <param name="logger">
        /// Receives log messages for significant events in the job scheduling
        /// system.
        /// </param>
        /// <param name="countPriorities">
        /// The number of priority classes for jobs.  This is typically
        /// a constant for the application.  The actual weights for
        /// each priority class are dynamically adjustable.
        /// </param>        
        public JobSchedulingSystem(ILogger<JobSchedulingSystem> logger, int countPriorities = 10)
        {
            _logger = logger;

            PriorityClasses = new(countPriorities, 
                () => new ClientQueueSystem<JobMessage, IJobQueueOwner, ClientQueue>(
                    key => new ClientQueue(),
                    new SimpleExpiryQueue(60000, 20)));

            for (int i = 0; i < countPriorities; ++i)
                PriorityClasses.ResetWeight(priority: i, weight: (i + 1) * 10);

            WorkerDistribution = new WorkerDistribution<PromiseJobFunction, PromiseData>();
            WorkerDistribution.CreateWorker(new DummyWorker(logger), 10);

            _jobRunnerTask = JobScheduling.RunJobsAsync(
                                PriorityClasses.AsChannel(),
                                WorkerDistribution.AsChannel(),
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
            GetJobToSchedule(ISchedulingAccount account, 
                             PromiseId promiseId,
                             PromiseJobFunction request, 
                             int charge,
                             out Task<PromiseData> outputTask)
        {
            JobMessage job;

            lock (_futures)
            {
                if (_futures.TryGetValue(promiseId, out var future) && !future.IsCancelled)
                {
                    job = future.CreateJob(account, CancellationToken.None);
                    outputTask = future.OutputTask;

                    // Check again because cancellation can race with registering
                    // a new client
                    if (!future.IsCancelled)
                        return job;
                }

                job = SharedFuture<PromiseJobFunction, PromiseData>.CreateJob(
                        request,
                        charge,
                        account,
                        CancellationToken.None,
                        _timingQueue,
                        out future);

                outputTask = future.OutputTask;
                _futures[promiseId] = future;
            }

            return job;
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
        /// is consumed, and <paramref name="jobFunction" /> is ignored.
        /// </param>
        /// <param name="jobFunction">
        /// Produces the result output of the promise.
        /// </param>
        public void PushJobForClientAsync(IJobQueueOwner owner,
                                          int priority, 
                                          int charge,
                                          Promise promise,
                                          PromiseJobFunction jobFunction)
        {
            // Enqueue nothing if promise is already completed
            if (promise.IsCompleted)
                return;

            var queue = PriorityClasses[priority].GetOrAdd(owner);

            var job = GetJobToSchedule(queue, 
                                       promise.Id, 
                                       jobFunction, 
                                       charge, 
                                       out var outputTask);

            promise.TryAwaitAndPostResult(new ValueTask<PromiseData>(outputTask),
                                          p => RemoveCachedFuture(p.Id));
            queue.Enqueue(job);
        }
    }
}
