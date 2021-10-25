﻿using System;
using Xunit;

using JobBank.Scheduling;
using JobBank.Utilities;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Threading.Tasks;
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

        /// <summary>
        /// Generate jobs according to some random number distributions,
        /// and queue them into the channels.
        /// </summary>
        private async Task<List<DummyJob>> QueueJobsIntoChannels(int jobCount,
                                                                 ChannelWriter<DummyJob>[] childWriters, 
                                                                 Func<double>[] randomGenerators)
        {
            var allJobsTogether = new List<DummyJob>(capacity: jobCount);
            var priorityHeap = new IntPriorityHeap<int>(null, capacity: 10);

            // Generate initial arrival times
            for (int childIndex = 0; childIndex < childWriters.Length; ++childIndex)
            {
                var waitTime = (int)Math.Round(randomGenerators[childIndex]());

                // Negate the keys because IntPriorityHeap is a maximum heap,
                // but we want to extract the earliest arrival time instead.
                priorityHeap.Insert(-waitTime, childIndex);
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

            return allJobsTogether;
        }

        /// <summary>
        /// Dequeue jobs from parent scheduling group and separate 
        /// them back into the child queues they came from.
        /// </summary>
        private async Task<List<DummyJob>[]> DequeueJobsFromChannel(int jobCount, 
                                                                    int childrenCount,
                                                                    ChannelReader<DummyJob> parentReader)
        {
            var jobsByChild = new List<DummyJob>[childrenCount];
            for (int i = 0; i < jobsByChild.Length; ++i)
                jobsByChild[i] = new List<DummyJob>(capacity: jobCount / jobsByChild.Length);

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

            return jobsByChild;
        }

        /// <summary>
        /// Verify that one (child) queue is working as expected
        /// by checking statistics on arrival and exit wait times.
        /// </summary>
        private void CheckWaitTimesStats(List<DummyJob> jobs, double theoWaitMean, double weight)
        {
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

            // Expected range of sample mean of exit wait times.
            //
            // This should be around weight × mean arrival time, where weight is
            // the proportion of time assigned to this queue multipled by the total
            // number of queues, because the jobs are all "executed" in serial.
            //
            // This is a weak test that all child flows are being de-queued fairly.
            Assert.InRange(exitWaitMean, low * weight, high * weight);
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

            var randomGenerators = new Func<double>[childWriters.Length];
            for (int i = 0; i < childWriters.Length; ++i)
                randomGenerators[i] = CreateExponentialWaitingTimeGenerator(seed: 500 + i, mean: theoWaitMean, cap: 1000.0);

            // Generate jobs to queue into children
            List<DummyJob> allJobsTogether = await QueueJobsIntoChannels(jobCount, 
                                                                         childWriters, 
                                                                         randomGenerators);

            var parentReader = parent.AsChannelReader();

            // There will be no more items added to child queues
            parent.TerminateChannelReader();

            // ... but we can still de-queue already added items
            Assert.False(parentReader.Completion.IsCompleted);

            // Dequeue jobs from parent and keep track of time
            List<DummyJob>[] jobsByChild = await DequeueJobsFromChannel(jobCount, 
                                                                        childWriters.Length, 
                                                                        parentReader);

            for (int childIndex = 0; childIndex < jobsByChild.Length; ++childIndex)
                CheckWaitTimesStats(jobsByChild[childIndex], theoWaitMean, weight: 10.0);
        }

        private static IEnumerable<double> GetSuccessiveDifferences(IEnumerable<double> numbers)
            => numbers.Zip(numbers.Skip(1), (x, y) => y - x);

        private static double GetSampleStandardDeviation(IReadOnlyCollection<double> numbers, double mean)
            => Math.Sqrt(numbers.Select(x => (x - mean) * (x - mean)).Sum() / (numbers.Count - 1));
    }
}
