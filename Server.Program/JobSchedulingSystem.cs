using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using JobBank.Scheduling;
using Microsoft.Extensions.Logging;

namespace JobBank.Server.Program
{
    using JobMessage = ScheduledJob<PromiseOutput, PromiseOutput>;
    using ClientQueue = SchedulingQueue<ScheduledJob<PromiseOutput, PromiseOutput>>;

    public class JobSchedulingSystem
    {
        public PrioritizedQueueSystem<
                JobMessage, 
                ClientQueueSystem<JobMessage, string, ClientQueue>>
            PriorityClasses { get; }

        public WorkerDistribution<PromiseOutput, PromiseOutput>
            WorkerDistribution { get; }

        /// <summary>
        /// Cached "future" objects for jobs that have been submitted to at
        /// least one job queue.
        /// </summary>
        private Dictionary<PromiseOutput, SharedFuture<PromiseOutput, PromiseOutput>>
            _futures = new();

        private class DummyWorker : IJobWorker<PromiseOutput, PromiseOutput>
        {
            public string Name => "DummyWorker";

            public async ValueTask<PromiseOutput> ExecuteJobAsync(uint executionId, 
                                                                  IRunningJob<PromiseOutput> runningJob,
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

            WorkerDistribution = new WorkerDistribution<PromiseOutput, PromiseOutput>();
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
                             PromiseOutput request, 
                             int charge)
        {
            JobMessage job;

            lock (_futures)
            {
                if (_futures.TryGetValue(request, out var future) && !future.IsCancelled)
                {
                    job = future.CreateJob(account, CancellationToken.None);

                    // Check again because cancellation can race with registering
                    // a new client
                    if (!future.IsCancelled)
                        return job;
                }

                job = SharedFuture<PromiseOutput, PromiseOutput>.CreateJob(
                        request,
                        charge,
                        account,
                        CancellationToken.None,
                        _timingQueue,
                        out future);

                _futures[request] = future;
            }

            return job;
        }

        public Task<PromiseOutput> PushJobForClientAsync(string client, int priority, PromiseOutput request, int charge)
        {
            var queue = PriorityClasses[priority].GetOrAdd(client);

            var job = GetJobToSchedule(queue, request, charge);
            queue.Enqueue(job);

            return job.Future.OutputTask;
        }
    }
}
