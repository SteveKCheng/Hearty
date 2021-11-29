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

        public ValueTask PushJobForClientAsync(string client, int priority, PromiseOutput request, int charge)
        {
            var queue = PriorityClasses[priority].GetOrAdd(client);

            var future = new SharedFuture<PromiseOutput, PromiseOutput>(
                            request,
                            charge,
                            CancellationToken.None,
                            queue,
                            _timingQueue);

            var job = future.CreateJob(queue);
            queue.Enqueue(job);

            return ValueTask.CompletedTask;
        }
    }
}
