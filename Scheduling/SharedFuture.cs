using Hearty.Utilities;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Scheduling
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
    /// In the general framework of Hearty, when a client requests for 
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
                                               , IJobCancellation
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
        /// Whether cancellation has occurred or is about to occur.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When all the participating clients at any one time
        /// have requested cancellation, this flag is set to true
        /// and remains that way.  The cancellation may not yet
        /// have followed through completely, i.e.  
        /// <see cref="OutputTask" /> may not
        /// yet be complete.  Nevertheless a new client that wants
        /// to run the same job can no longer share this instance,
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
        /// The number of clients that are currently interested
        /// in this future.
        /// </summary>
        public int NumberOfClients => Math.Max(_activeCount, 0);

        /// <summary>
        /// Triggers cancellation for this job, when all clients 
        /// agree to cancel, or when forced to do so.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This cancellation source provides the token that is passed
        /// to the job execution itself, i.e. <see cref="IJobWorker{TInput, TOutput}.ExecuteJobAsync" />.
        /// The cancellation tokens from the clients are not passed directly
        /// there but are effectively linked to this source.
        /// </para>
        /// This member is disposed, i.e. the cancellation source is 
        /// returned to the pool, only on successful completion of the job,
        /// to avoid races with triggering cancellation.
        /// </remarks>
        private CancellationSourcePool.Use _cancellationSourceUse;

        /// <remarks>
        /// This member is set to its default state if 
        /// <see cref="_account"/> becomes <see cref="_splitter" />
        /// which holds the full list of clients.
        /// </remarks>
        private ClientData _singleClientData;

        /// <summary>
        /// Registration entry for a client of <see cref="SharedFuture{TInput, TOutput}" />.
        /// </summary>
        private struct ClientData
        {
            /// <summary>
            /// Registration on client's original <see cref="CancellationToken" />
            /// to propagate it to <see cref="SharedFuture{TInput, TOutput}._cancellationSourceUse" />.
            /// </summary>
            public CancellationTokenRegistration CancellationRegistration;

            /// <summary>
            /// A function called to notify the client when it disengages
            /// from the job or the job finishes.
            /// </summary>
            public ClientFinishingAction? OnClientFinish;
        }

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
        public int EstimatedWait { get; }

        /// <inheritdoc cref="IRunningJob.LaunchStartTime" />
        public long LaunchStartTime => _startTime;

        /// <summary>
        /// The amount of time, in milliseconds, 
        /// recorded to have elapsed for the currently running job
        /// in the timer's last periodic update.
        /// </summary>
        private int _currentWait;

        /// <summary>
        /// The maximum of <see cref="_currentWait" /> and
        /// <see cref="EstimatedWait" /> when <see cref="_splitter" /> is
        /// null.
        /// </summary>
        /// <remarks>
        /// When <see cref="_splitter" /> is not null, it handles
        /// this difference and <see cref="_currentCharge" /> becomes
        /// always equal to <see cref="_currentWait" />.
        /// </remarks>
        private int _currentCharge;

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
        private SchedulingAccountSplitter<ClientData>? _splitter;

        /// <summary>
        /// Locked to ensure that variables related to time accounting 
        /// are consistent when concurrent callers consult them.
        /// </summary>
        /// <remarks>
        /// These are <see cref="_account" />, <see cref="_splitter" />,
        /// <see cref="_currentWait" />,
        /// <see cref="_currentCharge" />, <see cref="_accountingStarted" />
        /// <see cref="_cancellationRegistration" /> and
        /// <see cref="_activeCount" />.
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
            if (_activeCount <= 0)
                return false;

            int elapsed = MiscArithmetic.SaturateToInt(now - _startTime);

            lock (_accountLock)
            {
                // Do not update charges if job has completed.
                if (_activeCount <= 0)
                    return false;

                int currentWait = _currentWait;
                if (elapsed > currentWait)
                {
                    int resolution = EstimatedWait >= 100 ? EstimatedWait : 100;
                    int extraCharge = elapsed - currentWait;

                    // Round up extraCharge to closest unit of resolution,
                    // saturating on overflow.
                    int roundedCharge =
                        (extraCharge <= int.MaxValue - resolution)
                            ? (extraCharge + (resolution - 1)) / resolution * resolution
                            : int.MaxValue;

                    // Update to new value
                    int newWait = MiscArithmetic.SaturatingAdd(currentWait,
                                                               roundedCharge);
                    _currentWait = newWait;

                    // Invoke UpdateCurrentItem if new charge is higher.
                    int currentCharge = _currentCharge;
                    if (newWait > currentCharge)
                    {
                        _account.UpdateCurrentItem(currentCharge,
                                                   newWait - currentCharge);
                        _currentCharge = newWait;
                    }
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

            ClientData clientData;
            CancellationSourcePool.Use cancellationSourceUse = default;

            ISchedulingAccount account;
            int currentCharge;
            bool accountingStarted;

            lock (_accountLock)
            {
                account = _account;
                currentCharge = _currentCharge;
                accountingStarted = _accountingStarted;

                clientData = _singleClientData;
                _singleClientData = default;

                if (_activeCount > 0)
                {
                    _activeCount = -1;

                    // Dispose the cancellation source only when not
                    // requested to cancel, to avoid a race with
                    // the actual cancellation, which occurs without
                    // having _accountLock locked.
                    cancellationSourceUse = _cancellationSourceUse;
                    _cancellationSourceUse = default;
                }

                _currentCharge = _currentWait = elapsed;
            }

            // This can go outside the lock because _activeCount <= 0
            // means critical variables cannot ever be modified again.
            if (accountingStarted)
            {
                account.UpdateCurrentItem(currentCharge,
                                          elapsed - currentCharge);
            }

            // This will also remove all participants from _splitter
            account.TabulateCompletedItem(elapsed);

            UnregisterClient(account, clientData);
            cancellationSourceUse.Dispose();
        }

        /// <summary>
        /// Asynchronous task that furnishes the result
        /// when the job finishes executing.
        /// </summary>
        /// <remarks>
        /// Any functions to unregister clients and to accumulate
        /// statistics on <see cref="ISchedulingAccount" /> are
        /// guaranteed to have been invoked before this task 
        /// gets completed, thus ruling out some subtle race 
        /// conditions.
        /// </remarks>
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
        /// <param name="estimatedWait">The initial estimate of the
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
        /// <param name="onClientFinish">
        /// An optional function invoked to clean up for the first client 
        /// (represented by <paramref name="account" />) of this job 
        /// when it disengages from this job or when this job completes.
        /// This function is particularly useful because it will 
        /// receive a reference to the new job whereas the methods of
        /// <see cref="ISchedulingAccount" /> do not.
        /// </param>
        public SharedFuture(in TInput input, 
                            int estimatedWait, 
                            ISchedulingAccount account,
                            CancellationToken cancellationToken,
                            ClientFinishingAction? onClientFinish,
                            SimpleExpiryQueue timingQueue)
        {
            _account = account;
            _timingQueue = timingQueue;
            _activeCount = 1;

            // Touch this object to make sure it is created,
            // because the lazy creation is not thread-safe.
            _ = _taskBuilder.Task;

            Input = input;
            EstimatedWait = estimatedWait;

            _cancellationSourceUse = CancellationSourcePool.Rent();

            // The cancellation source just rented is never exposed,
            // so it can be used as a lock object.
            _accountLock = _cancellationSourceUse.Source!;

            _singleClientData = new ClientData
            {
                CancellationRegistration = RegisterForCancellation(cancellationToken),
                OnClientFinish = onClientFinish
            };
        }

        /// <summary>
        /// Register a callback on a client's cancellation token
        /// to propagate into <see cref="_cancellationSourceUse" />.
        /// </summary>
        private CancellationTokenRegistration
            RegisterForCancellation(CancellationToken cancellationToken)
            => cancellationToken.UnsafeRegister(
                static (s, t) => Unsafe.As<SharedFuture<TInput, TOutput>>(s!)
                                       .CancelForClient(t, background: true),
                this);

        /// <inheritdoc cref="IJobCancellation.CancelForClient" />
        public bool CancelForClient(CancellationToken clientToken, 
                                    bool background)
        {
            if (_activeCount <= 0)
                return false;

            CancellationTokenSource? cancellationSource = null;
            
            ISchedulingAccount? account = null;
            ClientData clientData = default;
            int countRemoved = 0;
            bool toTerminate;

            lock (_accountLock)
            {
                // Ignore cancellation if the job is already finished.
                if (_activeCount <= 0)
                    return false;

                var splitter = _splitter;

                if (_singleClientData.CancellationRegistration.Token == clientToken)
                {
                    countRemoved = 1;
                    clientData = _singleClientData;
                    _singleClientData = default;
                    account = _account;
                }
                else if (splitter is not null)
                {
                    countRemoved = splitter.RemoveParticipants(
                                    clientToken,
                                    (t, a, r) => r.CancellationRegistration.Token == t);

                    // Note that countRemoved == 0 may occur where the
                    // same cancellation token is registered onto more than
                    // one participant: then the first invocation of this
                    // method removes all participants for that cancellation
                    // token, and subsequent invocations for the same
                    // cancellation token see no more participants.
                    // The following code will do nothing then.
                }

                // Prepare to cancel outside _accountLock if all
                // participants have been removed.
                toTerminate = (_activeCount -= countRemoved) <= 0;
                if (toTerminate)
                    cancellationSource = _cancellationSourceUse.Source;
            }

            // This method does nothing if clientData has not been assigned
            // to a non-default value, not caring if account is null.
            UnregisterClient(account!, clientData);

            if (toTerminate)
            {
                TerminateIfUnstarted();
                cancellationSource?.CancelMaybeInBackground(_accountingStarted);
            }

            return countRemoved > 0;
        }

        /// <inheritdoc cref="IJobCancellation.Kill" />
        public void Kill(bool background)
        {
            if (_activeCount <= 0)
                return;

            CancellationTokenSource? cancellationSource = null;

            lock (_accountLock)
            {
                // Ignore cancellation if the job is already finished.
                if (_activeCount <= 0)
                    return;

                // If there are further requests to cancel, they may
                // be safely ignored.
                _activeCount = 0;

                // Just cancel the main cancellation source directly,
                // and let FinalizeCharge clean up everything else.
                cancellationSource = _cancellationSourceUse.Source;
            }

            TerminateIfUnstarted();

            background &= _accountingStarted;
            cancellationSource?.CancelMaybeInBackground(background);
        }

        /// <summary>
        /// Called to complete <see cref="OutputTask" /> and perform
        /// all clean-up if this future is cancelled before it
        /// even starts its job.
        /// </summary>
        private bool TerminateIfUnstarted()
        {
            if (Interlocked.Exchange(ref _jobLaunched, 1) != 0)
                return false;

            // At this point, FinalizeCharge cannot have been called
            // yet, so _cancellationSourceUse remains valid, even
            // if the token has been triggered already.
            var cancellationToken = _cancellationSourceUse.Token;

            Exception? exception = null;
            
            try
            {
                exception = new OperationCanceledException(cancellationToken);
                var now = _startTime = Environment.TickCount64;
                FinalizeCharge(now);
            }
            catch (Exception e)
            {
                exception ??= e;
            }

            _taskBuilder.SetException(exception);
            return true;
        }

        /// <summary>
        /// Function to be invoked on behalf of a client when 
        /// this future completes or the client cancels.
        /// </summary>
        /// <param name="future">The future that the client has
        /// registered itself into. 
        /// </param>
        /// <param name="account">The scheduling account for the client
        /// at the time of registration.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token for the client at the time of registration, 
        /// which might be used as an identifier as in <see cref="IJobCancellation" />.
        /// </param>
        public delegate void ClientFinishingAction(SharedFuture<TInput, TOutput> future,
                                                   ISchedulingAccount account,
                                                   CancellationToken cancellationToken);

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
        /// <param name="onClientFinish">
        /// An optional function invoked to clean up for the new client
        /// (represented by <paramref name="account" />) of this job 
        /// when it disengages from this job or when this job completes.
        /// This function is particularly useful because it will 
        /// receive a reference to the new job whereas the methods of
        /// <see cref="ISchedulingAccount" /> do not.
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
        /// must create a new instance of this class through <see cref="CreateJob" />.
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
        /// returns true.  When this method
        /// fails to attach the client because the future object has
        /// already been cancelled by its other clients, the return value
        /// is false.
        /// </returns>
        public bool TryShareJob(ISchedulingAccount account,
                                CancellationToken cancellationToken,
                                ClientFinishingAction? onClientFinish)
        {
            // Strictly speaking we are not supposed to use _accountLock,
            // which is a pooled object, after it has been returned to
            // the pool when FinalizeCharge completes.  The harm is most
            // likely minor, but still, avoid surprises by checking 
            // the status first outside the lock.
            if (_activeCount <= 0)
                return false;

            SchedulingAccountSplitter<ClientData>? splitter;
            lock (_accountLock)
            {
                // Do not bother to add participant if future is done.
                if (_activeCount <= 0)
                    return false;

                // Install a new SchedulingAccountSplitter unless it already
                // exists because this future object is already shared.
                splitter = _splitter;
                if (splitter is null)
                {
                    if (_accountingStarted)
                    {
                        int currentWait = _currentWait;
                        _currentCharge = currentWait;
                        splitter = new(EstimatedWait, currentWait, _account,
                                       _singleClientData, UnregisterClientAction, this);
                    }
                    else
                    {
                        splitter = new(UnregisterClientAction, this);
                        splitter.AddParticipant(_account, _singleClientData);
                    }

                    _singleClientData = default;
                    _account = _splitter = splitter;
                }

                ++_activeCount;
            }

            // Now, _activeCount must be at least 2.  The cancellation tokens
            // may have concurrently triggered, but from this object's point
            // of view, it can never be cancelled yet.
            //
            // Next, register cancellation processing.  If we are unlucky,
            // all clients, including the new one to add, may race to
            // cancel simultaneously as we execute the following code.  
            // The race is harmless and can be ignored, because splitter
            // below will deal with it correctly.
            var clientData = new ClientData
            {
                CancellationRegistration = RegisterForCancellation(cancellationToken),
                OnClientFinish = onClientFinish
            };

            // Finally, attach the new client for the purposes of charging
            // execution time. This call may re-adjusts charges on all accounts.
            //
            // As SchedulingAccountSplitter locks internally,
            // updates to charges are serialized and cannot be missed. 
            if (!splitter.AddParticipant(account, clientData))
            {
                UnregisterClient(account, clientData);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Unregister a client when it disengages or when this future
        /// is completed.
        /// </summary>
        private void UnregisterClient(ISchedulingAccount account, ClientData clientData)
        {
            var token = clientData.CancellationRegistration.Token;

            // Important: we do not call CancellationTokenRegistration.Dispose!
            // That method blocks until the callback is executed, which may
            // deadlock, when removal of a participant is triggered by the
            // registered cancellation callback in the first place!
            clientData.CancellationRegistration.Unregister();

            clientData.OnClientFinish?.Invoke(this, account, token);
        }

        /// <summary>
        /// Cached delegate of <see cref="UnregisterClient" /> to pass
        /// into <see cref="SchedulingAccountSplitter{TRegistration}" />.
        /// </summary>
        private static readonly Action<object?, ISchedulingAccount, ClientData>
            UnregisterClientAction = (s, a, d) =>
                Unsafe.As<SharedFuture<TInput, TOutput>>(s!).UnregisterClient(a, d);

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
            Exception? exception = null;
            TOutput output = default!;
            bool workerInvoked = false;

            try
            {
                // At this point, FinalizeCharge cannot have been called
                // yet, so _cancellationSourceUse remains valid, even
                // if the token has been triggered already.
                var cancellationToken = _cancellationSourceUse.Token;

                _startTime = Environment.TickCount64;

                if (cancellationToken.IsCancellationRequested)
                {
                    // If the job is already cancelled before work begins,
                    // act as if the job (message) does not even exist
                    // in the first place, e.g. as if it had been removed
                    // from its containing queue.  This check is not
                    // strictly necessary but is good for performance.
                    exception = new OperationCanceledException(cancellationToken);
                }
                else
                {
                    lock (_accountLock)
                    {
                        int currentCharge = _recordedInitialCharge * _activeCount;
                        _currentCharge = (_splitter is null) ? currentCharge : 0;
                        _accountingStarted = true;
                        _account.UpdateCurrentItem(null, currentCharge);
                    }

                    _timingQueue.Enqueue(
                        static (t, s) => Unsafe.As<SharedFuture<TInput, TOutput>>(s!)
                                               .UpdateCurrentCharge(t),
                        this);

                    cancellationToken.ThrowIfCancellationRequested();

                    workerInvoked = true;
                    output = await worker.ExecuteJobAsync(executionId, this, cancellationToken)
                                         .ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                exception = e;
            }

            try
            {
                if (!workerInvoked)
                {
                    try { worker.AbandonJob(executionId); }
                    catch { }
                }

                var endTime = Environment.TickCount64;
                FinalizeCharge(endTime);
            }
            catch (Exception e)
            {
                // FIXME What to do with exception here?
                exception ??= e;
            }

            if (exception is null)
                _taskBuilder.SetResult(output);
            else
                _taskBuilder.SetException(exception);
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

        /// <summary>
        /// Get the amount that this item should be charged
        /// when it is taken out of its scheduling flow.
        /// </summary>
        /// <returns>
        /// The value of <see cref="EstimatedWait" /> divided by the
        /// number of active clients, if non-zero.  The
        /// return value is zero if there are no
        /// active clients, meaning that the job has completed
        /// or has been cancelled.
        /// </returns>
        /// <remarks>
        /// This calculation is inherently racy because the result
        /// is queried, before the job is actually launched, when
        /// the number of active clients can change.  However, 
        /// the race is tolerated as this number is only an estimate
        /// to inform scheduling: the actual charge to the scheduling
        /// flows will get corrected eventually.
        /// </remarks>
        int ISchedulingExpense.GetInitialCharge()
        {
            int activeCount = _activeCount;
            if (activeCount <= 0)
                return 0;

            int initialCharge = EstimatedWait / activeCount;
            int value = Interlocked.CompareExchange(ref _recordedInitialCharge,
                                                    initialCharge, 0);
            return value != 0 ? value : initialCharge;
        }

        /// <summary>
        /// The value first calculated by 
        /// <see cref="ISchedulingExpense.GetInitialCharge" />.
        /// </summary>
        /// <remarks>
        /// This value needs to be recalled separately from <see cref="EstimatedWait" />
        /// so that when charge accounting starts in <see cref="LaunchJobInternalAsync" />
        /// the initial charge value is consistent with what has been reported
        /// from <see cref="ISchedulingExpense.GetInitialCharge" />,
        /// when this future is de-queued from the scheduling flow, even if
        /// clients raced to add or remove themselves in the window between
        /// the two method calls.
        /// </remarks>
        private int _recordedInitialCharge;
    }
}
