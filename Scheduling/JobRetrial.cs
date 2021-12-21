using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents work that should have executed (successfully) before
    /// and is being re-tried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When a job needs to be re-tried or quarantined, 
    /// it has to be re-queued as a distinct object since
    /// <see cref="SharedFuture{TInput, TOutput}" />, or
    /// more generally <see cref="ILaunchableJob{TInput, TOutput}" />,
    /// behaves like standard .NET tasks in that it is one-shot.
    /// </para>
    /// <para>
    /// The new object representing a job to retry points back
    /// to the original job object.  The original job object does
    /// not complete until the retries have occurred,
    /// whether successful or not.
    /// </para>
    /// </remarks>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing the job.
    /// </typeparam>
    internal class JobRetrial<TInput, TOutput> : ILaunchableJob<TInput, TOutput>
    {
        /// <summary>
        /// Refers back to the original job object before retrying or quarantine.
        /// </summary>
        public IRunningJob<TInput> OriginalJob { get; }

        /// <summary>
        /// The cancellation token used to execute the original job.
        /// </summary>
        public CancellationToken CancellationToken { get; }

        /// <inheritdoc cref="ILaunchableJob{TInput, TOutput}.OutputTask" />
        public Task<TOutput> OutputTask => _taskBuilder.Task;

        /// <inheritdoc cref="IRunningJob{TInput}.Input" />
        public TInput Input => OriginalJob.Input;

        /// <inheritdoc cref="IRunningJob.InitialWait" />
        public int InitialWait => OriginalJob.InitialWait;

        public JobRetrial(IRunningJob<TInput> originalJob, 
                              CancellationToken cancellationToken)
        {
            OriginalJob = originalJob;
            CancellationToken = cancellationToken;
        }

        /// <summary>
        /// Builds the task in <see cref="OutputTask" />.
        /// </summary>
        /// <remarks>
        private AsyncTaskMethodBuilder<TOutput> _taskBuilder;

        /// <summary>
        /// Set to one when the job is launched, to prevent
        /// it from being launched multiple times.
        /// </summary>
        private int _jobLaunched;

        /// <inheritdoc cref="ILaunchableJob{TInput, TOutput}.TryLaunchJo" />
        public bool TryLaunchJob(IJobWorker<TInput, TOutput> worker, uint executionId)
        {
            if (Interlocked.Exchange(ref _jobLaunched, 1) != 0)
            {
                worker.AbandonJob(executionId);
                return false;
            }

            _ = LaunchJobInternalAsync(worker, executionId);
            return true;
        }

        /// <summary>
        /// Launch this job on a worker and set the result of <see cref="OutputTask" />.
        /// </summary>
        private async Task LaunchJobInternalAsync(IJobWorker<TInput, TOutput> worker,
                                                  uint executionId)
        {
            try
            {
                var output = await worker.ExecuteJobAsync(executionId, this, CancellationToken)
                                         .ConfigureAwait(false);
                _taskBuilder.SetResult(output);
            }
            catch (Exception e)
            {
                _taskBuilder.SetException(e);
            }
        }
    }
}
