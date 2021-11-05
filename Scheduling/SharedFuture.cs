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
    public class SharedFuture<TInput, TOutput>
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
        /// Triggered when this job should be cancelled.
        /// </summary>
        /// <remarks>
        /// This cancellation token is shared among all potential producers.
        /// It is triggered only when all clients agree to cancel and
        /// not just one of them.  
        /// </remarks>
        public CancellationToken CancellationToken { get; }

        /// <summary>
        /// The initial estimate of the amount of time the job
        /// would take, in milliseconds.
        /// </summary>
        public int InitialCharge { get; }

        private int _currentCharge;

        public int CurrentCharge
        {
            get => _currentCharge;
            set
            {
                int change = MiscArithmetic.SaturatingSubtract(_currentCharge, value);
                _currentCharge = value;
                _owner.AdjustBalance(change);
            }
        }

        /// <summary>
        /// Asynchronous task that furnishes the result
        /// when the job finishes executing.
        /// </summary>
        public Task<TOutput> OutputTask => _taskBuilder.Task;

        public void SetResult(TOutput output)
            => _taskBuilder.SetResult(output);

        public void SetException(Exception exception)
            => _taskBuilder.SetException(exception);

        public SharedFuture(ISchedulingAccount owner,
                            in TInput input, 
                            int initialCharge, 
                            CancellationToken cancellationToken)
        {
            _owner = owner;

            Input = input;
            InitialCharge = initialCharge;
            CancellationToken = cancellationToken;
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
        public ScheduledJob<TInput, TOutput> CreateJob()
            => new ScheduledJob<TInput, TOutput>(this);

        /// <summary>
        /// Builds the task in <see cref="OutputTask" />.
        /// </summary>
        /// <remarks>
        /// This task builder is used to avoid allocating a separate <see cref="TaskCompletionSource{TResult}" />.
        /// This class here does not inherit from <see cref="TaskCompletionSource{TResult}" /> but we do not want to 
        /// expose that functionality in public.   For similar reasons this class does not simply
        /// implement <see cref="System.Threading.Tasks.Sources.IValueTaskSource{TResult}" />
        /// even if that could avoid one more allocation.
        /// </remarks>
        private AsyncTaskMethodBuilder<TOutput> _taskBuilder;

        /// <summary>
        /// For re-charging or crediting the originating queue for
        /// revised estimates of the execution time of the job.
        /// </summary>
        private readonly ISchedulingAccount _owner;
    }
}
