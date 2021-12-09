using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JobBank.Scheduling;
using Microsoft.Extensions.Logging;

namespace JobBank.Server.Program
{
    using JobMessage = ScheduledJob<PromiseData, PromiseData>;
    using ClientQueue = SchedulingQueue<ScheduledJob<PromiseData, PromiseData>>;

    public class JobSchedulingSystem
    {
        public PrioritizedQueueSystem<
                JobMessage, 
                ClientQueueSystem<JobMessage, string, ClientQueue>>
            PriorityClasses { get; }

        public WorkerDistribution<PromiseData, PromiseData>
            WorkerDistribution { get; }

        /// <summary>
        /// Cached "future" objects for jobs that have been submitted to at
        /// least one job queue.
        /// </summary>
        private Dictionary<PromiseId, SharedFuture<PromiseData, PromiseData>>
            _futures = new();

        private class DummyWorker : IJobWorker<PromiseData, PromiseData>
        {
            public string Name => "DummyWorker";

            public async ValueTask<PromiseData> ExecuteJobAsync(uint executionId, 
                                                                  IRunningJob<PromiseData> runningJob,
                                                                  CancellationToken cancellationToken)
            {
                _logger.LogInformation("Starting job for execution ID {executionId}", executionId);
                await Task.Delay(runningJob.InitialWait, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Completing job for execution ID {executionId}", executionId);
                return new Payload("application/json", Encoding.ASCII.GetBytes(@"{ ""status"": ""finished job"" }"));
            }

            public void AbandonJob(uint executionId)
            {
            }


            public DummyWorker(ILogger logger)
            {
                _logger = logger;
            }

            private readonly ILogger _logger;
        }

        private readonly ILogger _logger;

        public JobSchedulingSystem(ILogger<JobSchedulingSystem> logger)
        {
            _logger = logger;

            PriorityClasses = new(10, 
                () => new ClientQueueSystem<JobMessage, string, ClientQueue>(
                    EqualityComparer<string>.Default,
                    key => new ClientQueue(),
                    new SimpleExpiryQueue(60000, 20)));

            for (int i = 0; i < PriorityClasses.Count; ++i)
                PriorityClasses.ResetWeight(priority: i, weight: (i + 1) * 10);

            WorkerDistribution = new WorkerDistribution<PromiseData, PromiseData>();
            WorkerDistribution.CreateWorker(new DummyWorker(logger), 10, null);

            _jobRunnerTask = JobScheduling.RunJobsAsync(
                                PriorityClasses.AsChannel(),
                                WorkerDistribution.AsChannel(),
                                CancellationToken.None);
        }

        private readonly SimpleExpiryQueue _timingQueue
            = new SimpleExpiryQueue(1000, 50);

        private Task _jobRunnerTask;

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
                             PromiseData request, 
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

                job = SharedFuture<PromiseData, PromiseData>.CreateJob(
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
        /// <param name="client"></param>
        /// <param name="priority"></param>
        /// <param name="charge"></param>
        /// <param name="promise">
        /// A promise which may be newly created or already existing.
        /// If newly created, the asynchronous task for the job
        /// is posted into it.  Otherwise 
        /// </param>
        public void PushJobForClientAsync(string client, 
                                          int priority, 
                                          int charge,
                                          Promise promise)
        {
            // Enqueue nothing if promise is already completed
            if (promise.IsCompleted)
                return;

            var queue = PriorityClasses[priority].GetOrAdd(client);

            var job = GetJobToSchedule(queue, promise.Id, promise.RequestOutput!, charge, out var outputTask);

            promise.TryAwaitAndPostResult(new ValueTask<PromiseData>(outputTask),
                                          p => RemoveCachedFuture(p.Id));
            queue.Enqueue(job);
        }
    }
}
