using System;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents a claim to resources to execute a job in abstract
    /// job scheduling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This structure wraps a reference to <see cref="IJobWorker{TInput, TOutput}" />.
    /// That reference is not made accessible publicly to make it harder
    /// to manage execution resources incorrectly.
    /// </para>
    /// <para>
    /// If an instance of this structure is obtained but no job is 
    /// available to run, it should be disposed to release 
    /// any reserved resources for the job.
    /// </para>
    /// <para>
    /// This type is not thread-safe, and also it is "move-only".  
    /// Do not copy an instance and then proceed to call methods
    /// on both instances.
    /// </para>
    /// </remarks>
    /// <typeparam name="TInput">
    /// The inputs to execute a job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing a job.
    /// </typeparam>
    public struct JobVacancy<TInput, TOutput> : IDisposable
    {
        /// <summary>
        /// An arbitrary ID that can be assigned by the originator
        /// of this instance, for debugging and monitoring.
        /// </summary>
        public uint ExecutionId { get; }

        /// <summary>
        /// Name that identifies the worker being claimed, 
        /// for debugging and monitoring.
        /// </summary>
        public string? WorkerName => _worker?.Name;

        /// <summary>
        /// The worker which is being claimed for job execution.
        /// </summary>
        private IJobWorker<TInput, TOutput>? _worker;

        /// <summary>
        /// Use this vacancy to launch a scheduled job,
        /// unless the job was already launched.
        /// </summary>
        /// <returns>
        /// True if the job has just been launched; false
        /// if it had already been launched earlier.
        /// </returns>
        public bool TryLaunchJob(ILaunchableJob<TInput, TOutput> job)
        {
            var worker = _worker;
            if (worker is null)
                throw new ObjectDisposedException(nameof(JobVacancy<TInput, TOutput>));

            _worker = null;
            return job.TryLaunchJob(worker, ExecutionId);
        }

        /// <summary>
        /// Release any resources reserved by the worker,
        /// if no job is to be executed.
        /// </summary>
        public void Dispose()
        {
            var worker = _worker;
            if (worker is not null)
            {
                _worker = null;
                worker.AbandonJob(ExecutionId);
            }
        }

        /// <summary>
        /// Represent a claim to resources on a worker to launch a job.
        /// </summary>
        /// <param name="worker">The worker whose resources are being
        /// claimed to launch a job. </param>
        /// <param name="executionId">
        /// See <see cref="ExecutionId" />.
        /// </param>
        public JobVacancy(IJobWorker<TInput, TOutput> worker, 
                          uint executionId)
        {
            _worker = worker;
            ExecutionId = executionId;
        }
    }
}
