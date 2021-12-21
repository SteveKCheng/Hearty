using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// An abstract worker within <see cref="WorkerDistribution{TInput, TOutput}" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class generates the instances of <see cref="JobVacancy{TInput, TOutput}" />
    /// which feed <see cref="WorkerDistribution{TInput, TOutput}" />.
    /// </para>
    /// <para>
    /// This class also implements <see cref="IJobWorker{TInput, TOutput}" />
    /// to track the outstanding resource claims in abstract units.
    /// In typical usage, the abstract worker represents a node in a 
    /// computing cluster, and the units would correspond to the number
    /// of CPUs made available by that node.
    /// </para>
    /// <para>
    /// The reference to this class's implementation of
    /// <see cref="IJobWorker{TInput, TOutput}" /> is
    /// handed out through <see cref="JobVacancy{TInput, TOutput}" />.
    /// </para>
    /// </remarks>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing the job.
    /// </typeparam>
    internal sealed class DistributedWorker<TInput, TOutput>
        : SchedulingFlow<JobVacancy<TInput, TOutput>>
        , IJobWorker<TInput, TOutput>
        , IDistributedWorker<TInput>
    {
        /// <summary>
        /// The count of abstract resources that are currently 
        /// available to be claimed by new jobs.
        /// </summary>
        /// <remarks>
        /// As releasing a resource claim and taking one can race,
        /// this variable needs to be manipulated atomically.
        /// Alternatively locks could have been used.
        /// </remarks>
        private uint _resourceAvailable;

        /// <summary>
        /// The count of resources normally available to use
        /// concurrently, assuming each job takes one.
        /// </summary>
        private int _resourceTotal;

        /// <summary>
        /// 1 million divided <see cref="_resourceTotal"/>,
        /// used to deduct balances in a comparable way 
        /// when workers are heterogeneous.
        /// </summary>
        private int _inverseResourceTotal;

        /// <summary>
        /// ID assigned sequentially to each job execution.
        /// </summary>
        /// <remarks>
        /// In Job Bank's current default implementation, 
        /// this variable is never concurrently incremented
        /// because <see cref="TryTakeItem" /> is not invoked
        /// concurrently.  But, for future-proofing, we assume
        /// concurrency is possible, so interlocked operations
        /// are used on this variable.
        /// </remarks>
        private uint _executionId;

        /// <summary>
        /// Holds references to the jobs this worker is currently
        /// processing, for monitoring purposes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// As execution IDs are assigned sequentially, in 
        /// principle, a circular buffer could be used to map
        /// IDs to objects.  An open-addressed hashtable with
        /// the straightforward hash function essentially does
        /// the same thing, so that is used here for simplicity.
        /// .NET's implementation of <see cref="uint.GetHashCode"/>
        /// is already that straightforward hash function, but
        /// we can override it should the need arise.
        /// </para>
        /// <para>
        /// As with <see cref="_executionId" />, concurrent access
        /// to this data structure is expected to be rare but in
        /// principle possible, so all access must be locked.
        /// </para>
        /// </remarks>
        private readonly Dictionary<uint, IRunningJob<TInput>> _currentJobs;

        /// <inheritdoc cref="IDistributedWorker.Name" />
        public string Name { get; }

        IEnumerable<IRunningJob<TInput>> IDistributedWorker<TInput>.CurrentJobs
        {
            get
            {
                lock (_currentJobs)
                    return _currentJobs.Values.ToArray();
            }
        }

        int IDistributedWorker.TotalResources => _resourceTotal;

        int IDistributedWorker.AvailableResources => (int)_resourceAvailable;

        /// <inheritdoc />
        protected override bool TryTakeItem(
            [MaybeNullWhen(false)] out JobVacancy<TInput, TOutput> item,
            out int charge)
        {
            // Decrease _resourceAvailable if positive,
            // otherwise do not emit job vacancy.
            if (MiscArithmetic.InterlockedSaturatedDecrement(ref _resourceAvailable) == 0)
            {
                item = default;
                charge = default;
                return false;
            }

            // Increment execution ID atomically with wrapping
            uint e = unchecked(Interlocked.Increment(ref _executionId) - 1);

            item = new JobVacancy<TInput, TOutput>(this, e);
            charge = _inverseResourceTotal;
            return true;
        }

        /// <summary>
        /// Release a claim to a resource that a job has occupied earlier.
        /// </summary>
        private void ReplenishResource()
        {
            AdjustBalance(_inverseResourceTotal);

            // That this logic can work without a lock around the whole
            // operation requires subtle reasoning.  
            //
            // While _resourceAvailable is updated atomically, the request
            // to activate or to de-activate the child flow only has
            // internal locking.  So there can be a race with activating
            // and de-activating.
            //
            // There is no harm if the de-activation that SchedulingGroup
            // does when it obtains false from TryTakeItem happens first.
            // If activation happens first, that is also okay if it
            // happens after SchedulingGroup puts up its "guard" before
            // invoking TryTakeItem.  If de-activation happens before
            // the guard, that implies there is no race in the first place: 
            // TryTakeItem observes _resourceAvailable to be greater than
            // zero.
            if (Interlocked.Increment(ref _resourceAvailable) == 1)
                Activate();
        }

        /// <summary>
        /// Remove an entry from the list of current jobs being
        /// processed by this worker.
        /// </summary>
        private void RemoveCurrentJob(uint executionId)
        {
            lock (_currentJobs)
                _currentJobs.Remove(executionId);
        }

        void IJobWorker<TInput, TOutput>.AbandonJob(uint executionId)
        {
            ReplenishResource();
            RemoveCurrentJob(executionId);
            _executor.AbandonJob(executionId);
        }

        async ValueTask<TOutput> 
            IJobWorker<TInput, TOutput>.ExecuteJobAsync(uint executionId, 
                                                        IRunningJob<TInput> runningJob,
                                                        CancellationToken cancellationToken)
        {
            try
            {
                lock (_currentJobs)
                {
                    _currentJobs.Add(executionId, runningJob);
                }

                try
                {
                    return await _executor.ExecuteJobAsync(executionId, runningJob, cancellationToken)
                                          .ConfigureAwait(false);
                }
                catch (Exception e) when (_failedJobFallback != null)
                {
                    return await _failedJobFallback.Invoke(e, runningJob, cancellationToken)
                                                   .ConfigureAwait(false);
                }
                finally
                {
                    RemoveCurrentJob(executionId);
                }
            }
            finally
            {
                ReplenishResource();
            }
        }

        /// <summary>
        /// Implements the actual job action.
        /// </summary>
        private readonly IJobWorker<TInput, TOutput> _executor;

        /// <summary>
        /// Construct this wrapper to count resources claims 
        /// for executing jobs.
        /// </summary>
        /// <param name="name">
        /// The name of the worker.
        /// </param>
        /// <param name="totalResources">
        /// The count of abstract resources this worker is assumed to have.
        /// It is usually the number of CPUs, though there may be overcommit.
        /// </param>
        /// <param name="executor">
        /// Implements the actual job action.
        /// </param>
        /// <param name="failedJobFallback">
        /// User-specified fallback to invoke whenever any job fails on
        /// this worker.
        /// </param>
        public DistributedWorker(string name,
                                 int totalResources,
                                 IJobWorker<TInput, TOutput> executor,
                                 FailedJobFallback<TInput, TOutput>? failedJobFallback)
        {
            Name = name;

            _executor = executor;
            _failedJobFallback = failedJobFallback;

            if (totalResources <= 0 || totalResources > 100000)
                throw new ArgumentOutOfRangeException(nameof(totalResources));

            _resourceTotal = totalResources;
            _resourceAvailable = (uint)totalResources;
            _inverseResourceTotal = 1000000 / totalResources;

            _currentJobs = new Dictionary<uint, IRunningJob<TInput>>(_resourceTotal * 2);
        }

        public void Dispose()
        {
            // FIXME cancel in transient manner all jobs being processed by the worker
            LeaveParent();
        }

        /// <summary>
        /// User-specified fallback when a job fails, 
        /// inherited from <see cref="WorkerDistribution{TInput, TOutput}" />.
        /// </summary>
        private readonly FailedJobFallback<TInput, TOutput>? _failedJobFallback;
    }
}
