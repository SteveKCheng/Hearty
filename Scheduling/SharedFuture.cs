﻿using JobBank.Utilities;
using System;
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
    public class SharedFuture<TInput, TOutput> : ILaunchableJob<TInput, TOutput>
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
        /// Since the <see cref="CancellationTokenSource" /> may be internally
        /// recycled, for safety, the internal <see cref="CancellationToken" /> 
        /// to trigger the cancellation is not exposed directly.
        /// </para>
        /// <para>
        /// Furthermore, there is a race condition, owing to how
        /// cancellation is implemented, in that the cancellation
        /// token may trigger at a slightly later time then when
        /// this object considers itself cancelled, i.e. when all
        /// of its clients trigger their cancellation tokens
        /// (those passed in to <see cref="CreateJob" /> or
        /// <see cref="TryShareJob" />).
        /// </para>
        /// </remarks>
        public bool IsCancelled => (_activeCount == 0);

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
        /// <see cref="_account"/> is <see cref="_splitter" />
        /// which holds the full list of registrations.
        /// </remarks>
        private CancellationTokenRegistration _cancellationRegistration;

        /// <summary>
        /// Counts how many clients remain that have not cancelled.
        /// </summary>
        /// <remarks>
        /// This variable is less than zero if the job completes,
        /// i.e. <see cref="FinalizeCharge" /> is reached.
        /// </remarks>
        private int _activeCount;

        /// <summary>
        /// The initial estimate of the amount of time the job
        /// would take, in milliseconds.
        /// </summary>
        public int InitialWait { get; }

        /// <inheritdoc cref="IRunningJob.LaunchStartTime" />
        public long LaunchStartTime => _startTime;

        /// <summary>
        /// The amount of time for the currently running job,
        /// in milliseconds, that have been charged to all 
        /// participating accounts.
        /// </summary>
        private int _currentWait;

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
        /// to <see cref="_splitter" />.
        /// </remarks>
        private ISchedulingAccount _account;

        /// <summary>
        /// Set to true when <see cref="LaunchJobInternalAsync" />
        /// invokes <see cref="ISchedulingAccount.UpdateCurrentItem" />
        /// for the first time.
        /// </summary>
        /// <remarks>
        /// This flag is needed for a newly created <see cref="_splitter" />
        /// that replaces <see cref="_account" /> to correctly 
        /// imitate the old state of the latter.  
        /// </remarks>
        private bool _accountingStarted;

        /// <summary>
        /// Holds and manages multiple <see cref="ISchedulingAccount" />
        /// when this job is shared.
        /// </summary>
        private SchedulingAccountSplitter<CancellationTokenRegistration>? _splitter;

        /// <summary>
        /// Locked to ensure that variables related to time accounting 
        /// are consistent when concurrent callers consult them.
        /// </summary>
        /// <remarks>
        /// These are <see cref="_account" />, <see cref="_splitter" />,
        /// <see cref="_currentWait" />, <see cref="_accountingStarted" />
        /// and <see cref="_cancellationRegistration" />.
        /// </remarks>
        private readonly object _accountLock;

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

            lock (_accountLock)
            {
                // Do not update charges if job has completed.
                if (_activeCount <= 0)
                    return false;

                int currentWait = _currentWait;
                if (elapsed > currentWait)
                {
                    int resolution = InitialWait >= 100 ? InitialWait : 100;
                    int extraCharge = elapsed - currentWait;

                    // Round up extraCharge to closest unit of resolution,
                    // saturating on overflow.
                    int roundedCharge =
                        (extraCharge <= int.MaxValue - resolution)
                            ? (extraCharge + (resolution - 1)) / resolution * resolution
                            : int.MaxValue;

                    // Update to new value
                    _currentWait = MiscArithmetic.SaturatingAdd(currentWait,
                                                                roundedCharge);

                    _account.UpdateCurrentItem(currentWait, roundedCharge);
                }
            }

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

            CancellationTokenRegistration cancellationRegistration;

            lock (_accountLock)
            {
                var account = _account;
                var currentWait = _currentWait;

                _currentWait = elapsed;
                cancellationRegistration = _cancellationRegistration;
                _cancellationRegistration = default;
                _activeCount = -1;

                account.UpdateCurrentItem(currentWait, elapsed - currentWait);
                account.TabulateCompletedItem(elapsed);
            }

            cancellationRegistration.Dispose();
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
            _activeCount = 1;

            Input = input;
            InitialWait = initialCharge;

            _cancellationSourceUse = CancellationSourcePool.Rent();

            // The cancellation source just rented is never exposed,
            // so it can be used as a lock object.
            _accountLock = _cancellationSourceUse.Source!;

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
                // N.B. Existing cancellation callbacks are always disposed
                //      before the rented cancellation source is returned
                //      in FinalizeCharge.  So the cancellation source is
                //      guaranteed to be valid to use here.
                _cancellationSourceUse.Source!.Cancel();
            }
        }

        /// <summary>
        /// Create a new job to push into a job-scheduling queue. 
        /// </summary>
        /// <param name="input">The input describing the job. </param>
        /// <param name="initialCharge">The initial estimate of the
        /// time to execute the job, in milliseconds.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for the initial client that it can trigger
        /// when it is no longer interested in the returned job.  
        /// The job may remain running if it is subsequently 
        /// shared with other clients before the first client cancels.
        /// </param>
        /// <param name="account">
        /// Where to charge back for the time taken when the job is executed,
        /// There must be an "original" account, representing the initial
        /// client, passed as a parameter here.
        /// More accounts can share the charges by attaching them implicitly
        /// through <see cref="TryShareJob" />.
        /// </param>
        /// <param name="timingQueue">
        /// Used to periodically fire timers to update the estimate
        /// of the time needed to execute the job.
        /// </param>
        /// <param name="future">
        /// The instance of this class that has been created and set
        /// into the property <see cref="ScheduledJob{TInput, TOutput}.Future" />
        /// of the returned structure.
        /// </param>
        /// <returns>
        /// The new future object paired with the accounting information,
        /// generally used as a job message in a job queuing system.
        /// </returns>
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
        /// Atomically increment an integer variable only if it is positive.
        /// </summary>
        /// <param name="value">The variable to atomically increment. </param>
        /// <returns>
        /// Whether the variable has been successfully incremented.
        /// </returns>
        private static bool InterlockedIncrementIfPositive(ref int value)
        {
            int oldValue;
            int newValue = value;

            do
            {
                oldValue = newValue;
                if (oldValue <= 0)
                    return false;

                newValue = Interlocked.CompareExchange(ref value,
                                                       oldValue + 1,
                                                       oldValue);
            } while (newValue != oldValue);

            return true;
        }

        /// <summary>
        /// Represent an existing job for a new client, 
        /// to push into a job-scheduling queue. 
        /// </summary>
        /// <param name="account">
        /// Where to charge back for the time taken when the job is executed,
        /// for the new client. 
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for the new client that it can trigger
        /// when it is no longer interested in the returned job.  
        /// The job may remain running if the other clients of the same
        /// job have yet cancelled.
        /// </param>
        /// <remarks>
        /// <para>
        /// If the same job gets scheduled multiple times (by different clients),
        /// each time there is a different representative 
        /// (of type <see cref="ScheduledJob{TInput, TOutput}" />
        /// that points to the same instance of 
        /// <see cref="SharedFuture{TInput, TOutput}" />.
        /// </para>
        /// <para>
        /// There can be an unavoidable race condition in that this future object
        /// gets cancelled by current clients (i.e. <see cref="IsCancelled" />
        /// becomes true) while this method is attempting to attach a new client.
        /// When that happens, the caller, if it wants the cancelled job restarted,
        /// but create a new instance of this class through <see cref="CreateJob" />.
        /// </para>
        /// <para>
        /// The race condition can be reliably and correctly detected
        /// by checking <see cref="IsCancelled" /> to be true 
        /// (assuming <paramref name="cancellationToken" /> has not been triggered).
        /// When it happens, this method does not add the new client.
        /// </para>
        /// </remarks>
        /// <returns>
        /// When the client has been successfully attached, this method
        /// returns this same future object paired with the accounting 
        /// information of the new client.  The value is generally used as a 
        /// job message in a job queuing system.  When this method
        /// fails to attach the client because the future object has
        /// already been cancelled by its other clients, the return value
        /// is null.
        /// </returns>
        public ScheduledJob<TInput, TOutput>? 
            TryShareJob(ISchedulingAccount account,
                        CancellationToken cancellationToken)
        {
            SchedulingAccountSplitter<CancellationTokenRegistration>? splitter;
            lock (_accountLock)
            {
                // Do not bother to add participant if future is done.
                if (_activeCount <= 0)
                    return null;

                // Install a new SchedulingAccountSplitter unless it already
                // exists because this future object is already shared.
                splitter = _splitter;
                if (splitter is null)
                {
                    if (_accountingStarted)
                    {
                        splitter = new(InitialWait, _currentWait, _account,
                                       ref _cancellationRegistration);
                    }
                    else
                    {
                        splitter = new();
                        splitter.AddParticipant(_account, ref _cancellationRegistration);
                    }

                    _account = _splitter = splitter;
                }
            }

            // Increment _activeCount atomically unless it is already <= 0
            if (!InterlockedIncrementIfPositive(ref _activeCount))
                return null;

            // Now, _activeCount must be at least 2.  The cancellation tokens
            // may have concurrently triggered, but from this object's point
            // of view, it can never be cancelled yet.
            //
            // Next, register cancellation processing.  If we are unlucky,
            // all clients, including the new one to add, may race to
            // cancel simultaneously as we execute the following code.  
            // The race is harmless and can be ignored, because splitter
            // below will deal with it correctly.
            var cancelRegistration = RegisterForCancellation(cancellationToken);

            // Finally, attach the new client for the purposes of charging
            // execution time. This call may re-adjusts charges on all accounts.
            //
            // As SchedulingAccountSplitter locks internally,
            // updates to charges are serialized and cannot be missed. 
            if (!splitter.AddParticipant(account, ref cancelRegistration))
            {
                cancelRegistration.Dispose();
                return null;
            }

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
            var cancellationToken = _cancellationSourceUse.Token;

            // If the job is already cancelled before work begins,
            // act as if the job (message) does not even exist
            // in the first place, e.g. as if it had been removed
            // from its containing queue.  This check is not
            // strictly necessary but is good for performance.
            //
            // Fortunately, we do not need to dispose cancellation
            // registrations here, since they must have been
            // removed already or been cancelled, for the main
            // token to have been cancelled in the first place.
            if (cancellationToken.IsCancellationRequested)
            {
                worker.AbandonJob(executionId);
                _taskBuilder.SetException(
                    new OperationCanceledException(cancellationToken));
                return;
            }

            try
            {
                _startTime = Environment.TickCount64;

                lock (_accountLock)
                {
                    var initialCharge = InitialWait;
                    _currentWait = initialCharge;
                    _accountingStarted = true;
                    _account.UpdateCurrentItem(null, initialCharge);
                }

                _timingQueue.Enqueue(
                    static (t, s) => Unsafe.As<SharedFuture<TInput, TOutput>>(s!)
                                           .UpdateCurrentCharge(t),
                    this);

                cancellationToken.ThrowIfCancellationRequested();

                try
                {
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

        bool ILaunchableJob<TInput, TOutput>.TryLaunchJob(IJobWorker<TInput, TOutput> worker, 
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
