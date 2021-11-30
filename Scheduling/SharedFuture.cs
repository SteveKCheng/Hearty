using JobBank.Utilities;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Represents some result that should get produced in the future.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Colloquially this concept is also called a "job". 
    /// A job is not run until it is de-queued, and it should take minimal
    /// resources while it is still in the queue.  Furthermore, the job
    /// may be represented as declarative data only so that it can be serialized
    /// to a remote machine, whose connection details are not known until 
    /// the job gets de-queued.  
    /// </para>
    /// <para>
    /// For all these reasons, a scheduled job
    /// cannot be represented merely by <see cref="Task{TOutput}" />:
    /// it must be lazily evaluated, and this class can represent that "lazy"
    /// state of the job before it even starts.
    /// </para>
    /// <para>
    /// The term "shared" in the name of this class refers to that the 
    /// same job can be scheduled multiple times for execution, although
    /// obviously it should only be executed once, at the earliest 
    /// opportunity scheduled.  Consider that a <see cref="Task{TOutput}" />,
    /// or in general, a "promise", is set up for a single producer
    /// and multiple consumers (SPMC), of the result.  A "shared job",
    /// or in more formal computer science terminology, a shared "future",
    /// allows for multiple producers and a single consumer (MPSC).
    /// </para>
    /// <para>
    /// In the general framework of Job Bank, when a client requests for 
    /// a promise, and finds it has not completed, then it schedules
    /// a job ("future") to complete it.  There is usually this 
    /// correspondence between a producer and a consumer, but it is important
    /// to distinguish between the producer and consumer roles of the same
    /// client:
    /// <list type="bullet">
    /// <item>
    /// Firstly, a client might decide not to start a job but only consume
    /// it if it exists.  
    /// </item>
    /// <item>
    /// Secondly, if the client loses its connection, the server may still
    /// want to continue with producing results for the job, in case
    /// the client re-connects or any other client also wants it.
    /// </item>
    /// <item>
    /// When there are multiple clients, they will schedule the
    /// same job to produce the result at inevitably different times,
    /// depending on the clients' priorities within the job scheduling system.
    /// Of course, the earliest target scheduled time 
    /// that the job should win.  But, if the bookkeeping data structures
    /// do not distinguish between consumers and producers, then the job
    /// system would end up executing the job at the scheduled time 
    /// of the first client that attempts to schedule, which is not the
    /// same as earliest target scheduled time.  This subtle difference
    /// can cause priority inversion: if a slow client schedules
    /// a job first then a fast client, the fast client has to wait
    /// for the slow client to complete the job!
    /// </item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <typeparam name="TInput">
    /// The inputs to execute the job.
    /// </typeparam>
    /// <typeparam name="TOutput">
    /// The outputs from executing the job.
    /// </typeparam>
    public class SharedFuture<TInput, TOutput> : IRunningJob<TInput>
    {
        /// <summary>
        /// The inputs to execute the job.
        /// </summary>
        /// <remarks>
        /// Depending on the application, this member may be some delegate to run, 
        /// if the job is implemented entirely in-process.  Or it may be declarative 
        /// data, that may be serialized to run the job on a remote computer.  
        /// </remarks>
        public TInput Input { get; }

        /// <summary>
        /// Whether cancellation has been requested by all participating
        /// clients.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When all the participating clients at any one time
        /// have requested cancellation, this flag is set to true
        /// and remains that way.  The cancellation may not yet
        /// be processed, i.e.  <see cref="OutputTask" /> may not
        /// yet be complete.  Nevertheless a new client that wants
        /// to run the same job can no longer use this instance,
        /// and must remove it from its cache.
        /// </para>
        /// <para>
        /// Since the <see cref="CancellationTokenSource" /> is internally
        /// recycled, for safety, the internal <see cref="CancellationToken" /> 
        /// to trigger the cancellation is not exposed directly.
        /// </para>
        /// </remarks>
        public bool IsCancelled => (_activeCount <= 0);

        /// <summary>
        /// Triggers cancellation for this job when all clients agree to cancel.
        /// </summary>
        private CancellationSourcePool.Use _cancellationSourceUse;

        /// <summary>
        /// Registration on client's original <see cref="CancellationToken" />
        /// to propagate it to <see cref="_cancellationSourceUse"/>.
        /// </summary>
        /// <remarks>
        /// This member is set to its default state if 
        /// <see cref="_account"/> is <see cref="SchedulingAccountSplitter" />
        /// which holds the full list of registrations.
        /// </remarks>
        private CancellationTokenRegistration _cancellationRegistration;

        /// <summary>
        /// Counts how many clients remain that have not cancelled.
        /// </summary>
        private int _activeCount;

        /// <summary>
        /// The initial estimate of the amount of time the job
        /// would take, in milliseconds.
        /// </summary>
        public int InitialWait { get; }

        /// <summary>
        /// The amount of time for the currently running job,
        /// in milliseconds, that have been charged to all 
        /// participating accounts.
        /// </summary>
        private int _currentWait;

        /// <summary>
        /// Set to true when the job finishes.
        /// </summary>
        private bool _isDone;

        /// <summary>
        /// The snapshot of current time, as reported by
        /// <see cref="Environment.TickCount64" />, when this
        /// job has started running.
        /// </summary>
        private long _startTime;

        /// <summary>
        /// Charges back elapsed time to implement fair scheduling.
        /// </summary>
        /// <remarks>
        /// Occasionally there may be more than one account
        /// sharing the charge.  Then this member will be changed
        /// to an instance of <see cref="SchedulingAccountSplitter" />.
        /// </remarks>
        private ISchedulingAccount _account;

        /// <summary>
        /// Protects <see cref="ISchedulingAccount" />
        /// <see cref="_currentWait" />, and <see cref="_isDone" />.
        /// </summary>
        /// <remarks>
        /// Those two variables have to be consistent with each other.
        /// Note that this lock only protects the reference
        /// itself, in case it changes, not method calls to 
        /// <see cref="ISchedulingAccount" />, which is expected to 
        /// have its own internal locking.  A spin lock is used only
        /// to avoid having to allocate an extra object just for locking.
        /// </remarks>
        private SpinLock _accountLock = new SpinLock(enableThreadOwnerTracking: false);

        /// <summary>
        /// Arranges a timer that periodically fires to 
        /// adjust queue balances for the time taken so far to run the job.
        /// </summary>
        private readonly SimpleExpiryQueue _timingQueue;

        /// <summary>
        /// Called as an <see cref="ExpiryAction"/> so queue balances
        /// can be updated for the currntly running job.
        /// </summary>
        private bool UpdateCurrentCharge(long now)
        {
            int elapsed = MiscArithmetic.SaturateToInt(now - _startTime);

            ISchedulingAccount? account = null;
            int currentWait;
            int roundedCharge = 0;

            bool lockTaken = false;
            try
            {
                _accountLock.Enter(ref lockTaken);

                currentWait = _currentWait;

                // Do not update charges if job has completed.
                if (_isDone)
                    return false;

                if (elapsed > currentWait)
                {
                    int resolution = InitialWait >= 100 ? InitialWait : 100;
                    int extraCharge = elapsed - currentWait;

                    // Round up extraCharge to closest unit of resolution,
                    // saturating on overflow.
                    roundedCharge =
                        (extraCharge <= int.MaxValue - resolution)
                          ? (extraCharge + (resolution - 1)) / resolution * resolution
                          : int.MaxValue;

                    // Update to new value
                    _currentWait = MiscArithmetic.SaturatingAdd(currentWait, 
                                                                roundedCharge);

                    account = _account;
                }
            }
            finally
            {
                if (lockTaken)
                    _accountLock.Exit();
            }

            account?.UpdateCurrentItem(currentWait, roundedCharge);

            // Re-schedule timer as long as job has not completed
            return true;
        }

        /// <summary>
        /// Adjusts queue balances for the final measurement of the
        /// time taken by the job once it ends.
        /// </summary>
        private void FinalizeCharge(long now)
        {
            int elapsed = MiscArithmetic.SaturateToInt(now - _startTime);

            ISchedulingAccount account;
            int currentWait;
            CancellationTokenRegistration cancellationRegistration;

            bool lockTaken = false;
            try
            {
                _accountLock.Enter(ref lockTaken);

                account = _account;
                currentWait = _currentWait;
                _currentWait = elapsed;
                cancellationRegistration = _cancellationRegistration;
                _cancellationRegistration = default;
                _isDone = true;
            }
            finally
            {
                if (lockTaken)
                    _accountLock.Exit();
            }

            account.UpdateCurrentItem(currentWait, elapsed - currentWait);
            account.TabulateCompletedItem(elapsed);

            cancellationRegistration.Dispose();

            lock (_cancellationSourceUse.Source!)
                _cancellationSourceUse.Dispose();
        }

        /// <summary>
        /// Asynchronous task that furnishes the result
        /// when the job finishes executing.
        /// </summary>
        public Task<TOutput> OutputTask => _taskBuilder.Task;

        /// <summary>
        /// Set to one when the job is launched, to prevent
        /// it from being launched multiple times.
        /// </summary>
        private int _jobLaunched;

        /// <summary>
        /// Prepare a job that can eventually be launched on some worker.
        /// </summary>
        /// <param name="input">The input describing the job. </param>
        /// <param name="initialCharge">The initial estimate of the
        /// time to execute the job, in milliseconds.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the job.
        /// </param>
        /// <param name="account">
        /// Where to charge back for the time taken when the job is executed.
        /// There must be an "original" account, passed as a parameter here.
        /// More accounts can share the charges by adding them implicitly
        /// through <see cref="CreateJob(ISchedulingAccount)" />.
        /// </param>
        /// <param name="timingQueue">
        /// Used to periodically fire timers to update the estimate
        /// of the time needed to execute the job.
        /// </param>
        private SharedFuture(in TInput input, 
                             int initialCharge, 
                             ISchedulingAccount account,
                             CancellationToken cancellationToken,
                             SimpleExpiryQueue timingQueue)
        {
            _account = account;
            _timingQueue = timingQueue;

            Input = input;
            InitialWait = initialCharge;

            _cancellationSourceUse = CancellationSourcePool.Rent();
            _activeCount = 1;
            _cancellationRegistration = RegisterForCancellation(cancellationToken);
        }

        /// <summary>
        /// Register a callback on a client's cancellation token
        /// to propagate into <see cref="_cancellationSourceUse" />.
        /// </summary>
        private CancellationTokenRegistration
            RegisterForCancellation(CancellationToken cancellationToken)
            => cancellationToken.Register(
                s => Unsafe.As<SharedFuture<TInput, TOutput>>(s!).OnCancel(), 
                this);
            
        /// <summary>
        /// Propagates cancellation from clients' <see cref="CancellationToken" />
        /// when all clients cancel.
        /// </summary>
        private void OnCancel()
        {
            if (Interlocked.Decrement(ref _activeCount) == 0)
            {
                var source = _cancellationSourceUse.Source;
                if (source != null)
                {
                    lock (source)
                    {
                        var s = _cancellationSourceUse.Source;
                        s?.Cancel();
                    }
                }
            }
        }

        /// <summary>
        /// Create a new job <see cref="ScheduledJob{TInput, TOutput}" />
        /// to push into a job-scheduling queue. 
        /// </summary>
        /// <param name="input">The input describing the job. </param>
        /// <param name="initialCharge">The initial estimate of the
        /// time to execute the job, in milliseconds.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the job.
        /// </param>
        /// <param name="account">
        /// Where to charge back for the time taken when the job is executed.
        /// There must be an "original" account, passed as a parameter here.
        /// More accounts can share the charges by adding them implicitly
        /// through <see cref="CreateJob(ISchedulingAccount)" />.
        /// </param>
        /// <param name="timingQueue">
        /// Used to periodically fire timers to update the estimate
        /// of the time needed to execute the job.
        /// </param>
        public static ScheduledJob<TInput, TOutput> 
            CreateJob(in TInput input,
                      int initialCharge,
                      ISchedulingAccount account,
                      CancellationToken cancellationToken,
                      SimpleExpiryQueue timingQueue,
                      out SharedFuture<TInput, TOutput> future)
        {
            future = new SharedFuture<TInput, TOutput>(input,
                                                       initialCharge,
                                                       account,
                                                       cancellationToken,
                                                       timingQueue);
            return new ScheduledJob<TInput, TOutput>(future, account);
        }

        /// <summary>
        /// Create a representative of this job 
        /// <see cref="ScheduledJob{TInput, TOutput}" />
        /// to push into a job-scheduling queue. 
        /// </summary>
        /// <remarks>
        /// If the same job gets scheduled multiple times (by different clients),
        /// each time there is a different representative that points to the same
        /// <see cref="SharedFuture{TInput, TOutput}" />.
        /// </remarks>
        public ScheduledJob<TInput, TOutput> CreateJob(ISchedulingAccount account,
                                                       CancellationToken cancellationToken)
        {
            var existingAccount = _account;

            SchedulingAccountSplitter? splitter;

            // Install SchedulingAccountSplitter when there is more than
            // one account.  We need a retry loop because we do not 
            // want to hold a spin lock during object allocation and
            // initialization.
            while ((splitter = existingAccount as SchedulingAccountSplitter) is null)
            {
                int currentWait = _currentWait;
                splitter = new SchedulingAccountSplitter(existingAccount,
                                                         currentWait,
                                                         _cancellationRegistration);

                bool lockTaken = false;
                try
                {
                    _accountLock.Enter(ref lockTaken);

                    var a = _account;

                    if (_currentWait == currentWait && 
                        object.ReferenceEquals(a, existingAccount))
                    {
                        // Successful compare-exchange
                        _account = splitter;
                        _cancellationRegistration = default;
                        break;
                    }

                    existingAccount = a;
                }
                finally
                {
                    if (lockTaken)
                        _accountLock.Exit();
                }
            }

            // Increment _activeCount atomically unless it is already <= 0
            var activeCount = _activeCount;
            bool success;
            do
            {
                var c = activeCount;
                if (c <= 0)
                    break;
                activeCount = Interlocked.CompareExchange(ref _activeCount, c + 1, c);
                success = (activeCount == c);
            } while (!success);

            var cancellationRegistration =
                (activeCount > 0) ? RegisterForCancellation(cancellationToken)
                                  : default;

            // N.B. This call re-adjusts charges on all accounts,
            //      and so cannot be called during the retry loop.
            splitter.AddMember(account, cancellationRegistration);

            return new ScheduledJob<TInput, TOutput>(this, account);
        }

        /// <summary>
        /// Builds the task in <see cref="OutputTask" />.
        /// </summary>
        /// <remarks>
        /// This task builder is used to avoid allocating a separate <see cref="TaskCompletionSource{TResult}" />.
        /// This class here does not inherit from <see cref="TaskCompletionSource{TResult}" /> to avoid
        /// exposing the task-building functionality in public.   For similar reasons this class does not simply
        /// implement <see cref="System.Threading.Tasks.Sources.IValueTaskSource{TResult}" />
        /// even if that could avoid one more allocation.
        /// </remarks>
        private AsyncTaskMethodBuilder<TOutput> _taskBuilder;

        /// <summary>
        /// Launch this job on a worker and set the result of <see cref="OutputTask" />.
        /// </summary>
        private async Task LaunchJobInternalAsync(IJobWorker<TInput, TOutput> worker,
                                                  uint executionId)
        {
            try
            {
                var initialCharge = InitialWait;
                _startTime = Environment.TickCount64;

                bool lockTaken = false;
                ISchedulingAccount account;

                try
                {
                    _accountLock.Enter(ref lockTaken);
                    _currentWait = initialCharge;
                    account = _account;
                }
                finally
                {
                    if (lockTaken)
                        _accountLock.Exit();
                }
                
                account.UpdateCurrentItem(null, initialCharge);

                _timingQueue.Enqueue(
                    static (t, s) => Unsafe.As<SharedFuture<TInput, TOutput>>(s!)
                                           .UpdateCurrentCharge(t),
                    this);

                try
                {
                    var cancellationToken = _cancellationSourceUse.Token;
                    cancellationToken.ThrowIfCancellationRequested();

                    var output = await worker.ExecuteJobAsync(executionId, this, cancellationToken)
                                             .ConfigureAwait(false);
                    _taskBuilder.SetResult(output);
                }
                catch (Exception e)
                {
                    _taskBuilder.SetException(e);
                }
            }
            catch (Exception e)
            {
                worker.AbandonJob(executionId);
                _taskBuilder.SetException(e);
            }

            try
            {
                var endTime = Environment.TickCount64;
                FinalizeCharge(endTime);
            }
            catch
            {
                // FIXME What to do with exception here?
            }
        }

        /// <summary>
        /// Have a worker launch this job, but only if it has not been
        /// launched before.
        /// </summary>
        /// <param name="worker">
        /// The worker that would execute the job.
        /// </param>
        /// <param name="executionId">
        /// ID to identify the job execution for the worker, assigned by convention.
        /// </param>
        /// <returns>
        /// True if this job has been launched on <paramref name="worker" />;
        /// false if it was already launched (on another worker).
        /// </returns>
        internal bool TryLaunchJob(IJobWorker<TInput, TOutput> worker,
                                   uint executionId)
        {
            if (Interlocked.Exchange(ref _jobLaunched, 1) != 0)
            {
                worker.AbandonJob(executionId);
                return false;
            }

            _ = LaunchJobInternalAsync(worker, executionId);
            return true;
        }
    }
}
