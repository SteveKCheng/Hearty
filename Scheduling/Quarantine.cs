using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Scheduling
{
    /// <summary>
    /// Retries failed jobs with special care to isolate the problem
    /// automatically and maintain stability of (multi-threaded) workers.
    /// </summary>
    /// <typeparam name="TInput">
    /// The inputs to execute any one of the jobs.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing any one of the jobs.
    /// </typeparam>
    public class Quarantine<TInput, TOutput> : IAsyncDisposable
    {
        /// <summary>
        /// Holds the quarantined jobs.
        /// </summary>
        private readonly ConcurrentQueue<JobRetrial<TInput, TOutput>> 
            _queue = new();

        /// <summary>
        /// The number of jobs that have been enqueued but not yet completely
        /// processed by <see cref="ProcessJobsAsync" />.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This counter is increased/decreased atomically to prevent
        /// missed updates arising from jobs being enqueued at the same
        /// time as <see cref="ProcessJobsAsync" /> finishes running.
        /// </para>
        /// <para>
        /// <see cref="ProcessJobsAsync" /> is called as soon as this
        /// counter transitions from zero to one.
        /// </para>
        /// </remarks>
        private int _outstandingJobs = 0;

        public Quarantine()
        {
            Latency = 5000;
        }

        /// <summary>
        /// The current jobs-processing task which is saved so it
        /// can be awaited for disposal.
        /// </summary>
        /// <remarks>
        /// When disposed, this member becomes null.
        /// </remarks>
        private Task? _processJobsTask = Task.CompletedTask;

        /// <summary>
        /// The jobs-processing task that asynchronous disposal waits for.
        /// </summary>
        /// <remarks>
        /// This object is swapped out of <see cref="_processJobsTask" />,
        /// and saved so that multiple calls to <see cref="DisposeAsync" />
        /// return the same task to await.
        /// </remarks>
        private Task? _disposalTask;

        /// <summary>
        /// Whether this object has been disposed.
        /// </summary>
        private bool IsDisposed => _disposalTask != null;

        private void ThrowDisposedException()
        {
            throw new ObjectDisposedException(nameof(Quarantine<TInput, TOutput>));
        }

        /// <summary>
        /// Register a job to quarantine and retry later.
        /// </summary>
        /// <param name="originalJob">The failed job to retry. </param>
        /// <param name="cancellationToken">
        /// Cancellation token that <paramref name="originalJob" />
        /// was executed with.  It will be re-used for the job's retrial.
        /// </param>
        /// <remarks>
        /// This method is typically called from <see cref="FailedJobFallback{TInput, TOutput}" />
        /// to retry a job that has already failed (once).
        /// </remarks>
        public void PushJob(IRunningJob<TInput> originalJob,
                            CancellationToken cancellationToken)
        {
            if (IsDisposed)
                ThrowDisposedException();

            var item = new JobRetrial<TInput, TOutput>(originalJob, cancellationToken);
            _queue.Enqueue(item);
            if (Interlocked.Increment(ref _outstandingJobs) == 1)
            {
                var t = ProcessJobsAsync();
                if (Interlocked.Exchange(ref _processJobsTask, t) == null)
                    ThrowDisposedException();
            }
        }

        /// <summary>
        /// The minimum time to wait, in milliseconds, to allow more
        /// quarantined jobs to come in, to process together.
        /// </summary>
        /// <remarks>
        /// When a (multi-threaded) worker fails because of one
        /// job "crashing", it may cause concurrent jobs to fail 
        /// that would have been fine otherwise.  To optimize processing
        /// of multiple failures, jobs are de-queued in a batch.
        /// This property indicates the minimum time to wait
        /// to collect one batch of items.
        /// </remarks>
        public int Latency { get; }

        private ValueTask<IJobWorker<TInput, TOutput>> RentWorkerAsync()
        {
            return ValueTask.FromResult<IJobWorker<TInput, TOutput>>(null!);
        }

        private ValueTask ReturnWorkerAsync(IJobWorker<TInput, TOutput> worker)
        {
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Counter for execution IDs assigned to job retrials.
        /// </summary>
        private uint _executionId;

        /// <summary>
        /// Rents a worker to execute the job retrials that have been
        /// queued up.
        /// </summary>
        /// <remarks>
        /// For effective quarantine, the worker must be temporarily 
        /// taken out of the normal pool of resources for executing jobs.
        /// So this procedure is only done when the queue of job retrials
        /// is non-empty, and at most one worker is rented at any instant.
        /// </remarks>
        private async Task ProcessJobsAsync()
        {
            // Ensure the Task object is returned as soon as possible
            // so it can be swapped into _processJobsTask.
            await Task.Yield();

            if (IsDisposed)
                return;

            var startTime = Environment.TickCount64;
            var worker = await RentWorkerAsync().ConfigureAwait(false);
            try
            {
                var timeTaken = Environment.TickCount64 - startTime;

                // Wait to gather as many concurrent failing jobs as possible
                if (timeTaken < Latency)
                    await Task.Delay(Latency - (int)timeTaken).ConfigureAwait(false);

                var items = new List<JobRetrial<TInput, TOutput>>(capacity: _queue.Count);
                var comparer = new JobLaunchStartTimeComparer(ascending: false);

                int count;

                do
                {
                    while (_queue.TryDequeue(out var item))
                    {
                        if (IsDisposed)
                            return;

                        items.Add(item);
                    }

                    // Sort jobs by descending launch time, so that the 
                    // jobs that are causing trouble are likely to be re-tried first.
                    items.Sort(comparer);

                    foreach (var item in items)
                    {
                        if (IsDisposed)
                            return;

                        var executionId = unchecked(_executionId++);
                        item.TryLaunchJob(worker, executionId);
                        try
                        {
                            await item.OutputTask.ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                        }
                    }

                    count = items.Count;
                    items.Clear();
                } while (Interlocked.Add(ref _outstandingJobs, -count) > 0);
            }
            finally
            {
                await ReturnWorkerAsync(worker).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Stops jobs from being registered further for retrial.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            lock (_queue)
            {
                var t = _disposalTask;
                if (t == null)
                    _disposalTask = t = Interlocked.Exchange(ref _processJobsTask, null);

                return new ValueTask(t!);
            }
        }
    }
}
