using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Hearty.Scheduling
{
    /// <summary>
    /// Pushes out job vacancies (opportunities to execute jobs)
    /// as resources become available from a set of workers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A worker in this context usually refers to one node
    /// in a cluster of computers which can accept jobs to
    /// execute from a central "Hearty" server.
    /// </para>
    /// <para>
    /// The most basic way to assign workers to jobs is round-robin:
    /// workers take turns in a fixed order.  Round-robin 
    /// comports fairly well when the workers are homogenous.  
    /// Furthermore, if the workers themselves have internal
    /// parallelization, i.e. if the work for a job can 
    /// optionally be split to different threads in the worker
    /// node.  When the total number of threads exceeds the
    /// number of jobs, then round-robin would tend to split 
    /// the threads equally among the jobs, leaving no thread
    /// wasted idling.
    /// </para>
    /// <para>
    /// However, straight round-robin does not work well
    /// if the workers are heterogenous.  For example, some
    /// machines in the cluster may have 128 CPUs while others
    /// have 256.  Then, for efficient multi-thread-aware distribution,
    /// workers with 256 CPUs should be filling jobs at
    /// double the rate of the workers with 128 CPUs. 
    /// </para>
    /// <para>
    /// Such distribution can be implemented by credit allocation
    /// similar to how jobs themselves are de-queued.  The implementation
    /// will definitely be sub-optimal in that it requires O(log N) time 
    /// to select the appropriate worker, while round-robin, obviously, 
    /// only requires O(1) time.  However, round-robin requires
    /// new code to manage the linked list of workers.  For the sake
    /// of faster development of this framework, the round-robin 
    /// implementation is omitted.
    /// </para>
    /// <para>
    /// This class also allows the status of the distributed workers
    /// to be observed.  For simplicity, each worker must be assigned
    /// a unique string key, which might be a name of the running
    /// host or of a pod in Kubernetes.
    /// </para>
    /// </remarks>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing the job.
    /// </typeparam>
    public class WorkerDistribution<TInput, TOutput> : IReadOnlyDictionary<string, IDistributedWorker<TInput>>
    {
        /// <summary>
        /// Applies fair scheduling to job vacancies.
        /// </summary>
        private readonly SchedulingGroup<JobVacancy<TInput, TOutput>> 
            _schedulingGroup;

        public WorkerDistribution()
        {
            _schedulingGroup = new SchedulingGroup<JobVacancy<TInput, TOutput>>(50)
            {
                RetainBalanceAfterReactivation = true
            };
            _allWorkers = new ConcurrentDictionary<string, DistributedWorker<TInput, TOutput>>();
        }

        private readonly ConcurrentDictionary<string, 
                                              DistributedWorker<TInput, TOutput>> _allWorkers;

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Keys"/>
        public IEnumerable<string> Keys => _allWorkers.Keys;

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Values"/>
        public IEnumerable<IDistributedWorker<TInput>> Values => _allWorkers.Values;

        /// <inheritdoc cref="IReadOnlyCollection{T}.Count" />
        public int Count => _allWorkers.Count;

        /// <summary>
        /// Get the object representing the 
        /// worker that has been registered under the given name.
        /// </summary>
        public IDistributedWorker<TInput> this[string key] => _allWorkers[key];

        /// <summary>
        /// Register a worker to participate in this distribution system.
        /// </summary>
        /// <param name="executor">
        /// Executes jobs assigned to the new worker.
        /// </param>
        /// <param name="concurrency">
        /// The degree of concurrency of the new worker, i.e. how many
        /// jobs it can run simultaneously.
        /// </param>
        /// <param name="worker">
        /// On return, this argument is set to the
        /// object that represents the new or existing worker
        /// among all the other workers managed by this class.
        /// </param>
        /// <returns>
        /// True if the worker has been successfully added;
        /// false if a worker with the same name already exists.
        /// </returns>
        public bool TryCreateWorker(IJobWorker<TInput, TOutput> executor,
                                    int concurrency,
                                    [MaybeNullWhen(false)] out IDistributedWorker<TInput> worker)
                                 
        {
            var name = executor.Name;
            var workerImpl = new DistributedWorker<TInput, TOutput>(name, 
                                                                    concurrency,
                                                                    executor,
                                                                    FailedJobFallback);
            if (!_allWorkers.TryAdd(name, workerImpl))
            {
                worker = default;
                return false;
            }

            _schedulingGroup.AdmitChild(workerImpl, activate: true);

            workerImpl.OnEvent += (object? sender, WorkerEventArgs e) =>
            {
                if (e.Kind == WorkerEventKind.Shutdown)
                    RemoveWorker(name);
            };

            worker = workerImpl;
            return true;
        }

        /// <summary>
        /// Remove a worker that had previously been added.
        /// </summary>
        /// <param name="name">
        /// The name of the worker as was passed to <see cref="TryCreateWorker" />.
        /// </param>
        /// <returns>
        /// True if the worker existed with that name and has been removed.
        /// False if there is no worker with that name.
        /// </returns>
        public bool RemoveWorker(string name)
        {
            if (!_allWorkers.TryRemove(name, out var worker))
                return false;

            worker.Dispose();
            return true;
        }

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.ContainsKey"/>
        public bool ContainsKey(string key)
            => _allWorkers.ContainsKey(key);

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.TryGetValue"/>
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out IDistributedWorker<TInput> value)
        {
            bool exists = _allWorkers.TryGetValue(key, out var worker);
            value = worker;
            return exists;
        }

        /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />
        public IEnumerator<KeyValuePair<string, IDistributedWorker<TInput>>> GetEnumerator()
        {
            var enumerator = _allWorkers.GetEnumerator();

            while (enumerator.MoveNext())
            {
                var item = enumerator.Current;
                yield return new KeyValuePair<string, IDistributedWorker<TInput>>(item.Key, item.Value);
            }
        }

        /// <inheritdoc cref="IEnumerable.GetEnumerator" />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Obtains the channel that emits job vacancies as soon as they
        /// become available from the distributed workers.
        /// </summary>
        public ChannelReader<JobVacancy<TInput, TOutput>> AsChannel()
            => _schedulingGroup.AsChannelReader();

        /// <summary>
        /// Stop receiving job vacancies from the channel returned by
        /// <see cref="AsChannel" />.
        /// </summary>
        public void TerminateChannel() => _schedulingGroup.TerminateChannelReader();

        /// <summary>
        /// User-specified fallback when a distributed worker fails to execute
        /// a job.
        /// </summary>
        public FailedJobFallback<TInput, TOutput>? FailedJobFallback { get; set; }
    }

    /// <summary>
    /// User-specified fallback when a distributed worker fails to execute
    /// a job.
    /// </summary>
    /// <remarks>
    /// The failing job may be logged, and optionally re-tried,
    /// by inserting a (new) representative of it into some queue.
    /// </remarks>
    /// <param name="exception">Reports the failure from the original job. </param>
    /// <param name="originalJob">The job that has failed. </param>
    /// <param name="cancellationToken">The cancellation token
    /// used to execute the job. 
    /// </param>
    /// <returns>
    /// Provides the result of the job, usually asynchronously 
    /// if the job is to be re-tried.  If the job is to be failed
    /// right away, the exception to report back can be wrapped
    /// synchronously.
    /// </returns>
    public delegate ValueTask<TOutput>
        FailedJobFallback<TInput, TOutput>(Exception exception,
                                           IRunningJob<TInput> originalJob,
                                           CancellationToken cancellationToken);
}
