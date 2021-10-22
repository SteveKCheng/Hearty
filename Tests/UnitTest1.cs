using System;
using Xunit;

using JobBank.Scheduling;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace JobBank.Tests
{
    internal class DummyJob : ISchedulingExpense
    {
        public int InitialCharge => 0;

        public int Id { get; }

        public DummyJob(int id)
        {
            Id = id;
        }
    }

    internal class BasicSchedulingGroup : SchedulingGroup<DummyJob>
    {
        public BasicSchedulingGroup()
            : base(100)
        {
        }

        public void AdmitChild(SchedulingUnit<DummyJob> child)
            => base.AdmitChild(child, activate: true);

        public new ChannelReader<DummyJob> AsChannelReader()
            => base.AsChannelReader();

        public new void TerminateChannelReader()
            => base.TerminateChannelReader();
    }

    public class UnitTest1
    {
        [Fact]
        public async Task Test1()
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
                        await writer.WriteAsync(new DummyJob(newJobId));
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
