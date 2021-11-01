using System;
using Xunit;

using JobBank.Scheduling;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;

namespace JobBank.Tests
{
    [DebuggerDisplay("Id: {Id} Child: {ChildIndex} Charge: {InitialCharge} Arrival: {ArrivalTime} Exit: {ExitTime}")]
    internal class DummyJob : ISchedulingExpense
    {
        public int InitialCharge { get; init; }

        public int Id { get; init; }

        public int ArrivalTime { get; init; }

        public int ExitTime { get; set; }

        public int ChildIndex { get; init; }
    }

    internal class BasicSchedulingGroup : SchedulingGroup<DummyJob>
    {
        public BasicSchedulingGroup()
            : base(100)
        {
            // Get easier to read numbers when debugging
            BalanceRefillAmount = 10000;
        }

        public void AdmitChild(SchedulingFlow<DummyJob> child)
            => base.AdmitChild(child, activate: true);

        public void AdmitChild(SchedulingFlow<DummyJob> child, int weight)
        {
            base.AdmitChild(child, activate: true);
            base.ResetWeight(child, weight, reset: false);
        }
    }

    internal class SchedulingStats
    {
        public double ArrivalWaitMean { get; init; }

        public double ExitWaitMean { get; init; }

        public double ArrivalWaitStdev { get; init; }

        public double ExitWaitStdev { get; init; }

        public double ArrivalWaitMeanError { get; init; }

        public double ExitWaitMeanError { get; init; }
    }

    public partial class SchedulingTests
    {
        [Fact]
        public async Task WriteToChildQueuesInParallel()
        {
            var cancellationSource = new CancellationTokenSource();
            var cancellationToken = cancellationSource.Token;
            var subtasks = new List<Task>();

            var parent = new BasicSchedulingGroup();

            var childWriters = new ChannelWriter<DummyJob>[10];
            for (int i = 0; i < childWriters.Length; ++i)
            {
                var channel = Channel.CreateUnbounded<DummyJob>();
                var child = new SimpleJobQueue<DummyJob>(channel.Reader);
                parent.AdmitChild(child);
                childWriters[i] = channel.Writer;
            }

            var parentReader = parent.AsChannelReader();
            int countJobsReceived = 0;

            var parentTask = Task.Run(async delegate
            {
                while (await parentReader.WaitToReadAsync())
                {
                    while (parentReader.TryRead(out var job))
                        ++countJobsReceived;
                }
            });

            int jobId = 0;
            for (int i = 0; i < childWriters.Length; ++i)
            {
                var writer = childWriters[i];

                subtasks.Launch(async delegate
                {
                    var random = new Random(i + 500);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int newJobId = Interlocked.Increment(ref jobId);
                        await writer.WriteAsync(new DummyJob { 
                            Id = jobId,
                            ChildIndex = i
                        });
                        await Task.Delay(random.Next(minValue: 5, maxValue: 250));
                    }
                }, cancellationToken);
            }

            await Task.Delay(10000);
            cancellationSource.Cancel();

            await Task.WhenAll(subtasks);
            parent.TerminateChannelReader();

            await parentTask;
            
        }
    }

    internal static class LaunchTaskExtensions
    {
        public static void Launch(this List<Task> allTasks, Func<Task> action, CancellationToken cancellationToken = default)
        {
            allTasks.Add(Task.Run(action, cancellationToken));
        }
    }
}
