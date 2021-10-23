using System;
using Xunit;

using JobBank.Scheduling;
using JobBank.Utilities;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace JobBank.Tests
{
    public partial class SchedulingTests
    {
        private Func<double> CreateExponentialWaitingTimeGenerator(int seed, double mean, double cap)
        {
            var uniformGenerator = new Random(seed);
            return () =>
            {
                var y = uniformGenerator.NextDouble();
                var t = -Math.Log(1.0 - y) * mean;
                t = Math.Min(t, cap);
                return t;
            };
        }

        [Fact]
        public async Task SimulateExactly()
        {
            var parent = new BasicSchedulingGroup();

            var childWriters = new ChannelWriter<DummyJob>[10];
            for (int i = 0; i < childWriters.Length; ++i)
            {
                var channel = Channel.CreateUnbounded<DummyJob>();
                var child = new SimpleJobQueue<DummyJob>(channel.Reader);
                parent.AdmitChild(child);
                childWriters[i] = channel.Writer;
            }

            int jobCount = 10000;

            double theoWaitMean = 200.0;

            var allJobsTogether = new List<DummyJob>(capacity: jobCount);

            // Generate jobs to queue into children
            {
                var randomGenerators = new Func<double>[childWriters.Length];
                for (int i = 0; i < childWriters.Length; ++i)
                    randomGenerators[i] = CreateExponentialWaitingTimeGenerator(seed: 500 + i, mean: theoWaitMean, cap: 1000.0);

                var priorityHeap = new IntPriorityHeap<int>(null, capacity: 10);

                // Generate initial arrival times
                for (int i = 0; i < childWriters.Length; ++i)
                {
                    var waitTime = (int)Math.Round(randomGenerators[i]());

                    // Negate the keys because IntPriorityHeap is a maximum heap,
                    // but we want to extract the earliest arrival time instead.
                    priorityHeap.Insert(-waitTime, i);
                }

                // Take the queue with the earliest arrival time, queue the job
                // for that, then generate the next arrival time for another job
                // on that queue.  Repeat jobCount times.
                for (int jobIndex = 0; jobIndex < jobCount; ++jobIndex)
                {
                    (int time, int childIndex) = priorityHeap.TakeMaximum();
                    var waitTime = (int)Math.Round(randomGenerators[childIndex]());
                    priorityHeap.Insert(time - waitTime, childIndex);

                    var job = new DummyJob
                    {
                        Id = jobIndex,
                        ChildIndex = childIndex,
                        ArrivalTime = -time,
                        InitialCharge = waitTime
                    };

                    // Keep jobs in one big sorted list for debugging
                    allJobsTogether.Add(job);

                    await childWriters[childIndex].WriteAsync(job);
                }
            }

            var parentReader = parent.AsChannelReader();
            var jobsByChild = new List<DummyJob>[childWriters.Length];
            for (int i = 0; i < jobsByChild.Length; ++i)
                jobsByChild[i] = new List<DummyJob>(capacity: jobCount / jobsByChild.Length);

            // There will be no more items added to child queues
            parent.TerminateChannelReader();

            // ... but we can still de-queue already added items
            Assert.False(parentReader.Completion.IsCompleted);

            // Dequeue jobs from parent and keep track of time
            {
                int currentTime = -1;
                //int lastArrivalTime = -1;

                for (int jobIndex = 0; jobIndex < jobCount; ++jobIndex)
                {
                    Assert.True(await parentReader.WaitToReadAsync());

                    // Ensure all the jobs we put in are there
                    bool hasJob = parentReader.TryRead(out var job);
                    Assert.True(hasJob);

                    //Assert.True(lastArrivalTime <= job!.ArrivalTime);
                    //lastArrivalTime = job.ArrivalTime;

                    currentTime = Math.Max(currentTime, job!.ArrivalTime);
                    currentTime += job.InitialCharge;
                    job.ExitTime = currentTime;

                    jobsByChild[job.ChildIndex].Add(job);
                }

                // Ensure there are no stray items (due to bugs in de-queuing)
                Assert.False(parentReader.TryRead(out _));

                // Channel should be signaling completion immediately
                var waitReadTask = parentReader.WaitToReadAsync();
                Assert.True(waitReadTask.IsCompletedSuccessfully &&
                            waitReadTask.Result == false);
                Assert.True(parentReader.Completion.IsCompletedSuccessfully);
            }

            for (int childIndex = 0; childIndex < jobsByChild.Length; ++childIndex)
            {
                var jobs = jobsByChild[childIndex];
                var arrivalWaitTimes = GetSuccessiveDifferences(jobs.Select(item => (double)item.ArrivalTime)).ToList();
                var exitWaitTimes = GetSuccessiveDifferences(jobs.Select(item => (double)item.ExitTime)).ToList();

                Assert.All(arrivalWaitTimes, dt => Assert.True(dt >= 0));
                Assert.All(exitWaitTimes, dt => Assert.True(dt >= 0));

                // Sample estimates
                var arrivalWaitMean = arrivalWaitTimes.Average();
                var exitWaitMean = exitWaitTimes.Average();

                // Standard deviations
                var arrivalWaitStDev = GetSampleStandardDeviation(arrivalWaitTimes, arrivalWaitMean);
                var exitWaitStDev = GetSampleStandardDeviation(exitWaitTimes, exitWaitMean);

                // Standard error of sample mean
                var arrivalWaitMeanError = arrivalWaitStDev / Math.Sqrt((double)arrivalWaitTimes.Count);
                var exitWaitMeanError = exitWaitStDev / Math.Sqrt((double)exitWaitTimes.Count);
                
                // Check sample mean of arrival wait times is consistent with
                // the mean of the theoretical random distribution
                double low = theoWaitMean - 3.0 * arrivalWaitMeanError;
                double high = theoWaitMean + 3.0 * arrivalWaitMeanError;
                Assert.InRange(arrivalWaitMean, low, high);

                // Expected range of sample mean of exit wait times
                // should be allJobs.Length × because the jobs are all "executed" in serial.
                // This is a weak test that all child flows are being de-queued fairly.
                Assert.InRange(exitWaitMean, low * jobsByChild.Length, high * jobsByChild.Length);
            }
        }

        private static IEnumerable<double> GetSuccessiveDifferences(IEnumerable<double> numbers)
            => numbers.Zip(numbers.Skip(1), (x, y) => y - x);

        private static double GetSampleStandardDeviation(IReadOnlyCollection<double> numbers, double mean)
            => Math.Sqrt(numbers.Select(x => (x - mean) * (x - mean)).Sum() / (numbers.Count - 1));
    }
}
