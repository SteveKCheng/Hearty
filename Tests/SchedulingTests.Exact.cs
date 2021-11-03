using System;
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
        private static Func<double> 
            CreateExponentialWaitingTimeGenerator(int seed, double mean, double cap)
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
        private static async Task<List<DummyJob>> 
            QueueJobsIntoChannels(int jobCount,
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
        private static async Task<List<DummyJob>[]> DequeueJobsFromChannel(int jobCount, 
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
                job.StartTime = currentTime;

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

        private static SchedulingStats CalculateSchedulingStats(List<DummyJob> jobs)
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

            return new SchedulingStats
            {
                ArrivalWaitMean = arrivalWaitMean,
                ExitWaitMean = exitWaitMean,
                ArrivalWaitStdev = arrivalWaitStDev,
                ExitWaitStdev = exitWaitStDev,
                ArrivalWaitMeanError = arrivalWaitMeanError,
                ExitWaitMeanError = exitWaitMeanError
            };
        }

        private static int GetChildQueueWeight(int i)
            => i switch
            {
                0 => 1,
                1 => 2,
                2 => 3,
                3 => 4,  // sum: 10
                4 => 5,
                5 => 5,  // sum: 20
                6 => 10,
                7 => 10, // sum: 40
                8 => 20, // sum: 60
                9 => 40, // sum: 100
                _ => 10
            };

        [Fact]
        public Task SimulateExactlyEqualWeight()
            => SimulateExactly(unequalWeight: false);

        [Fact]
        public Task SimulateExactlyUnequalWeight()
            => SimulateExactly(unequalWeight: true);

        public async Task SimulateExactly(bool unequalWeight)
        {
            var parent = new BasicSchedulingGroup();

            var childWriters = new ChannelWriter<DummyJob>[10];
            for (int i = 0; i < childWriters.Length; ++i)
            {
                var channel = Channel.CreateUnbounded<DummyJob>();
                var child = new SimpleJobQueue<DummyJob>(channel.Reader);

                int weight = unequalWeight ? GetChildQueueWeight(i) : 20;
                parent.AdmitChild(child, weight);
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

            // Get all statistics first to aid debugging
            var statsByChild = jobsByChild.Select(CalculateSchedulingStats).ToArray();

            foreach (var stats in statsByChild)
            {
                // Check sample mean of arrival wait times is consistent with
                // the mean of the theoretical random distribution
                double low = theoWaitMean - 3.0 * stats.ArrivalWaitMeanError;
                double high = theoWaitMean + 3.0 * stats.ArrivalWaitMeanError;
                Assert.InRange(stats.ArrivalWaitMean, low, high);
            
                if (!unequalWeight)
                {
                    // Expected range of sample mean of exit wait times.
                    //
                    // This should be around N × mean arrival time,
                    // because the jobs are all "executed" in serial.
                    //
                    // This is a weak test that all child flows are being de-queued fairly.
                    int N = childWriters.Length;
                    Assert.InRange(stats.ExitWaitMean, low * N, high * N);
                }
            }

            // When there are unequal weights, certain queues will complete
            // before others, because we do not continually inject items
            // at a certain rate.  That is, other queues will start to drain
            // faster as the heavier-weighted queues complete.
            //
            // So, the expected exit wait times for each queue is not simply
            // N × mean arrival time × its weight proportion.  We need a
            // iterative formula to compute the expected exit wait times.
            if (unequalWeight)
            {
                var multipliers = ComputeRelativeFinishTimes(childWriters.Length,
                                                             GetChildQueueWeight);
                Assert.All(statsByChild.Zip(multipliers),
                           arg =>
                           {
                               var (stats, multiplier) = arg;
                               double low = theoWaitMean - 3.0 * stats.ArrivalWaitMeanError;
                               double high = theoWaitMean + 3.0 * stats.ArrivalWaitMeanError;

                               Assert.InRange(stats.ExitWaitMean,
                                              multiplier * low,
                                              multiplier * high);
                           });
            }
        }
        
        /// <summary>
        /// Compute the times that queues finish draining assuming
        /// they start with the same amount of work but de-queue
        /// at different rates implied by their weights.
        /// </summary>
        /// <returns>
        /// Array of finish times, scaled relative to the time
        /// needed to drain a queue if it were to be assigned
        /// full use of the available resources.
        /// </returns>
        private static double[] ComputeRelativeFinishTimes(int numWeights,
                                                           Func<int, int> weightsFunc)
        {
            double[] waitTimes = new double[numWeights];

            (int Index, double Weight)[] weightsSorted = 
                Enumerable.Range(0, numWeights)
                          .Select(i => (i, (double)weightsFunc(i)))
                          .OrderByDescending(item => item.Item2)
                          .ToArray();

            double[] workRemaining = new double[numWeights];
            for (int i = 0; i < workRemaining.Length; ++i)
                workRemaining[i] = 1.0;

            double weightsSum = weightsSorted.Select(item => item.Weight).Sum();

            // Iterate through the queues in reverse order of their weights,
            // i.e. the order in which they should completely drain
            for (int i = 0; i < weightsSorted.Length; )
            {
                var (index, weight) = weightsSorted[i];

                // The amount of time that this child queue must have
                // taken to finish its remaining work given the fraction
                // of resources it has been allocated
                double time = workRemaining[index] * (weightsSum / weight);

                double newWeightsSum = weightsSum;

                // Update running wait times for all queues that have
                // not yet finished
                for (int j = i; j < weightsSorted.Length; ++j)
                {
                    var (otherIndex, otherWeight) = weightsSorted[j];

                    // Accumulate wait time for this time step
                    waitTimes[otherIndex] += time;

                    // Calculate the work remaining for the other queue
                    // given it has worked for this time step at a 
                    // (different) speed implied by otherWeight
                    workRemaining[otherIndex] -= time * (otherWeight / weightsSum);

                    // Queues with the same (highest) weight should finish
                    if (otherWeight == weight)
                    {
                        newWeightsSum -= weight;
                        ++i;
                    }
                }

                weightsSum = newWeightsSum;
            }

            return waitTimes;
        }

        /// <summary>
        /// Return the indefinite integral of the work done
        /// in the simulation.
        /// </summary>
        /// <param name="jobs">
        /// The list of jobs processed by one child queue, in order.
        /// </param>
        /// <returns>
        /// The indefinite integral, with initial condition set as zero
        /// at the start of the simulation.
        /// </returns>
        private static Func<double, double> 
            ComputeWorkIntegral(IReadOnlyList<DummyJob> jobs)
        {
            var knots = new KeyValuePair<int, int>[jobs.Count];

            int currentTime = int.MinValue;

            int runningIntegral = 0;
            int countKnots = 0;

            // Calculate the knots of the indefinite integral,
            // which is piecewise linear.
            foreach (var job in jobs)
            {
                Assert.InRange(job.StartTime, currentTime, job.ExitTime);

                int timeTaken = job.ExitTime - job.StartTime;

                if (job.StartTime != currentTime || countKnots == 0)
                    knots[countKnots++] = new(job.StartTime, runningIntegral);
                else
                    countKnots--;   // merge with preceding knot

                runningIntegral += timeTaken;
                knots[countKnots++] = new(job.ExitTime, runningIntegral);

                currentTime = job.ExitTime;
            }

            // Indefinite integral is identically zero if there are no jobs
            if (countKnots == 0)
                return (double t) => 0.0;

            // Return piecewise-linear interpolant
            return (double t) =>
            {
                var knotsSpan = knots.AsSpan().Slice(0, countKnots);
                int index = BinarySearch(knotsSpan, (int)t, false);
                if (index == 0)
                {
                    // Extrapolate as flat on the extreme left
                    return 0.0;
                }
                else if (index < countKnots)
                {
                    // Linearly interpolate
                    (int t_left, int y_left) = knotsSpan[index - 1];
                    (int t_right, int y_right) = knotsSpan[index];
                    var slope = (double)(y_right - y_left) / (double)(t_right - t_left);
                    return slope * (t - (double)t_left) + (double)y_left;
                }
                else
                {
                    // Extrapolate as flat on the extreme right
                    return (double)knotsSpan[^1].Value;
                }
            };
        }

        private static int BinarySearch<TKey, TValue>(
            Span<KeyValuePair<TKey, TValue>> sorted, 
            TKey target,
            bool forUpperBound)
        {
            // The closed interval [left,right] brackets the returned index
            int left = 0;
            int right = sorted.Length;

            var comparer = Comparer<TKey>.Default;

            // Bisect until the interval brackets only one choice of index
            while (left != right)
            {
                int mid = (left + right) >> 1;

                int comparison = comparer.Compare(sorted[mid].Key, target);
                    
                if (comparison < 0 || (forUpperBound && (comparison == 0)))
                    left = mid + 1;
                else
                    right = mid;
            }

            return left;
        }

        private static IEnumerable<double> GetSuccessiveDifferences(IEnumerable<double> numbers)
            => numbers.Zip(numbers.Skip(1), (x, y) => y - x);

        private static double GetSampleStandardDeviation(IReadOnlyCollection<double> numbers, double mean)
            => Math.Sqrt(numbers.Select(x => (x - mean) * (x - mean)).Sum() / (numbers.Count - 1));
    }
}
