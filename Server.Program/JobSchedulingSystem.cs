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

        private class DummyWorker : IJobWorker<PromiseOutput, PromiseOutput>
        {
            public string Name => "DummyWorker";

            public async ValueTask<PromiseOutput> ExecuteJobAsync(uint executionId, PromiseOutput input, CancellationToken cancellationToken)
            {
                try
                {
                    _logger.LogInformation("Starting job for execution ID {executionId}", executionId);
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Completing job for execution ID {executionId}", executionId);
                    return new Payload("application/json", Encoding.ASCII.GetBytes(@"{ ""status"": ""finished job"" }"));
                }
                finally
                {
                    ReplenishResource();
                }
            }

            public void AbandonJob(uint executionId)
            {
                ReplenishResource();
            }

            private void ReplenishResource()
            {
                _channelWriter.WriteAsync(new(this, unchecked(_executionId++)));
            }

            public DummyWorker(ILogger logger, ChannelWriter<JobVacancy<PromiseOutput, PromiseOutput>> channelWriter)
            {
                _logger = logger;
                _channelWriter = channelWriter;

                ReplenishResource();
            }

            private uint _executionId;

            private readonly ChannelWriter<JobVacancy<PromiseOutput, PromiseOutput>> _channelWriter;

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

            var vacanciesChannel = Channel.CreateBounded<JobVacancy<PromiseOutput, PromiseOutput>>(new BoundedChannelOptions(20)
            {
                AllowSynchronousContinuations = true,
                SingleReader = true,
                SingleWriter = true
            });

            var worker = new DummyWorker(logger, vacanciesChannel.Writer);

            _jobRunnerTask = JobScheduling.RunJobsAsync(
                                PriorityClasses.AsChannel(),
                                vacanciesChannel.Reader,
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

            var job = future.CreateJob();
            queue.Enqueue(job);

            return ValueTask.CompletedTask;
        }
    }
}
