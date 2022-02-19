using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JobBank.Scheduling;
using JobBank.Utilities;

namespace JobBank.Server;

using JobMessage = ScheduledJob<PromisedWork, PromiseData>;
using MacroJobExpansion = IAsyncEnumerable<(PromiseRetriever, PromisedWork)>;

using MacroJobLinks = CircularListLinks<MacroJobMessage, MacroJobMessage.ListLinksAccessor>;

/// <summary>
/// The message type put into the queues managed by
/// <see cref="JobSchedulingSystem" /> that implements
/// a "macro job".
/// </summary>
/// <remarks>
/// <para>
/// A "macro job" expands into many "micro jobs" only when
/// the macro job is de-queued.  This feature avoids 
/// having to push hundreds and thousands of messages 
/// (for the micro jobs) into the queue which makes it
/// hard to visualize and manage.  Resources can also
/// be conserved if the generator of the micro jobs
/// is able to, internally, represent micro jobs more 
/// efficiently than generic messages in the job
/// scheduling queues.
/// </para>
/// <para>
/// A user-supplied generator
/// lists out the micro jobs as <see cref="PromisedWork" />
/// descriptors, and this class transforms them into
/// the messages that are put into the job queue,
/// to implement job sharing and time accounting.
/// </para>
/// </remarks>
internal sealed class MacroJobMessage : IAsyncEnumerable<JobMessage>
{
    private readonly ClientJobQueue _queue;

    internal struct ListLinksAccessor : IInteriorStruct<MacroJobMessage, MacroJobLinks>
    {
        public ref MacroJobLinks GetInteriorReference(MacroJobMessage target)
            => ref target._listLinks;
    }

    private MacroJobLinks _listLinks;
    private readonly MacroJob _master;

    /// <summary>
    /// Provides the cancellation token for all micro jobs
    /// spawned from this macro job.
    /// </summary>
    /// <remarks>
    /// This token allows the micro jobs to be cancelled along
    /// with this macro job when only this macro job is to be
    /// cancelled, even if the cancellation token passed externally
    /// is linked to other operations.
    /// </remarks>
    private CancellationSourcePool.Use _rentedCancellationSource;

    /// <summary>
    /// Links the cancellation token passed in the constructor
    /// into <see cref="_rentedCancellationSource" />.
    /// </summary>
    private CancellationTokenRegistration _cancellationRegistration;

    /// <summary>
    /// Set to non-zero when this instance is no longer valid
    /// as a job message. 
    /// </summary>
    /// <remarks>
    /// <para>
    /// This variable is flagged to non-zero as soon as 
    /// <see cref="GetAsyncEnumerator" />
    /// is called, to disallow multiple calls.
    /// </para>
    /// <para>
    /// Enumeration of micro jobs can only be done once per instance
    /// of this class, because the jobs involve registrations that have
    /// to be accessed outside of the enumerator and cleaned up after
    /// the jobs finish executing.  Obviously, these registrations
    /// cannot be scoped to the enumerator instance.
    /// </para>
    /// <para>
    /// This variable is also flagged to non-zero (true) 
    /// when <see cref="_master" /> is already cancelled before
    /// this instance can register itself.
    /// </para>
    /// </remarks>
    private int _isInvalid;

    /// <summary>
    /// True when this instance is valid to enumerate (once),
    /// thus starting the job (when it is de-queued from the job queue).
    /// </summary>
    public bool IsValid => (_isInvalid == 0);

    /// <summary>
    /// Construct the macro job message.
    /// </summary>
    /// <param name="queue">
    /// The job queue that micro jobs will be pushed into.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for the macro job.
    /// For efficiency, all the micro jobs expanded from
    /// this macro job will share the same cancellation source,
    /// and micro jobs cannot be cancelled independently
    /// of one another.
    /// </param>
    internal MacroJobMessage(MacroJob master,
                             ClientJobQueue queue,
                             CancellationToken cancellationToken)
    {
        _master = master;
        _queue = queue;
        _rentedCancellationSource = CancellationSourcePool.Rent();

        _cancellationRegistration = cancellationToken.Register(
            static s => Unsafe.As<MacroJobMessage>(s!).Cancel(),
            this);

        _listLinks = new(this);
        _master.AddChild(this);
    }

    public void Cancel()
    {
        lock (this)
        {
            // Lock against concurrent disposal in CleanUpAsync
            _rentedCancellationSource.Source?.Cancel();
        }
    }

    /// <inheritdoc cref="IAsyncEnumerable{T}.GetAsyncEnumerator" />
    public async IAsyncEnumerator<JobMessage>
        GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isInvalid, 1) != 0)
        {
            throw new NotSupportedException(
                "Enumerator for MacroJobMessage may not be retrieved " +
                "more than once. ");
        }

        int count = 0;
        Exception? exception = null;
        CancellationToken jobCancelToken = _rentedCancellationSource.Token;

        JobSchedulingSystem jobScheduling = _master.JobScheduling;
        IPromiseListBuilder resultBuilder = _master.ResultBuilder;

        // Whether an exception indicates cancellation from the token
        // passed into this enumerator.
        static bool IsLocalCancellation(Exception e, CancellationToken c)
            => e is OperationCanceledException ec && ec.CancellationToken == c;

        //
        // We cannot write the following loop as a straightforward
        // "await foreach" with "yield return" inside, because
        // we need to catch exceptions.  We must control the
        // enumerator manually.
        //

        IAsyncEnumerator<(PromiseRetriever, PromisedWork)>? enumerator = null;
        try
        {
            // Do not do anything if another producer has already completed.
            if (resultBuilder.IsComplete)
            {
                _master.RemoveChild(this);
                yield break;
            }

            if (!jobCancelToken.IsCancellationRequested)
                enumerator = _master.Expansion.GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception e) when (!IsLocalCancellation(e, cancellationToken))
        {
            exception = e;
        }

        if (enumerator is not null)
        {
            while (true)
            {
                JobMessage? message;

                try
                {
                    if (jobCancelToken.IsCancellationRequested)
                        break;

                    // Stop generating job messages if another producer
                    // has completely done so, or there are no more micro jobs.
                    if (resultBuilder.IsComplete ||
                        !await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;

                    if (jobCancelToken.IsCancellationRequested)
                        break;

                    var (promiseRetriever, input) = enumerator.Current;

                    message = jobScheduling.RegisterJobMessage(
                                    _queue.SchedulingAccount,
                                    promiseRetriever,
                                    input,
                                    jobCancelToken,
                                    out var promise);

                    // Add the new member to the result sequence.
                    resultBuilder.SetMember(count, promise);
                    count++;
                }
                catch (Exception e) when (!IsLocalCancellation(e, cancellationToken))
                {
                    exception = e;
                    break;
                }

                // Do not schedule work if promise is already complete
                if (message is not null)
                    yield return message.GetValueOrDefault();
            }

            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (!IsLocalCancellation(e, cancellationToken))
            {
                exception ??= e;
            }
        }

        if (jobCancelToken.IsCancellationRequested && exception is null)
        {
            // Do not need to block waiting for our callback to complete
            // because cancellation source has already been triggered,
            // and it cannot be returned to the pool anyway.
            _cancellationRegistration.Unregister();
            _cancellationRegistration = default;

            // Complete resultBuilder with the cancellation exception
            // only if this producer is the last to cancel.
            if (_master.RemoveChild(this))
            {
                exception = new OperationCanceledException(jobCancelToken);
                resultBuilder.TryComplete(count, exception);
            }
        }
        else
        {
            // When this producers finishes successfully without
            // job cancellation, complete resultBuilder.
            resultBuilder.TryComplete(count, exception);
            _ = CleanUpAsync();
        }
    }

    /// <summary>
    /// Wait for all promises to complete before executing successful
    /// clean-up action.
    /// </summary>
    private async Task CleanUpAsync()
    {
        try
        {
            await _master.ResultBuilder.WaitForAllPromisesAsync()
                                       .ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            // We do not need to block on unregistering, because
            // the Cancel method already locks to prevent the
            // cancellation source from going away concurrently.
            _cancellationRegistration.Unregister();
            _cancellationRegistration = default;

            lock (this)
            {
                _rentedCancellationSource.Dispose();
            }
        }
        catch
        {
        }

        _master.RemoveChild(this);
    }
}

/// <summary>
/// Object which is shared by instances of <see cref="MacroJobMessage" />
/// that refer to the same macro job.
/// </summary>
internal sealed class MacroJob
{
    public readonly JobSchedulingSystem JobScheduling;
    public readonly IPromiseListBuilder ResultBuilder;
    public readonly PromiseId PromiseId;
    public readonly MacroJobExpansion Expansion;

    private MacroJobMessage? _children;
    private int _count;

    /// <summary>
    /// Construct with information about the job 
    /// shared instances of <see cref="MacroJobMessage" />.
    /// </summary>
    /// <param name="jobScheduling">
    /// The job scheduling system that this message is for.
    /// This reference is needed to push micro jobs into
    /// the job queue.
    /// </param>
    /// <param name="resultBuilder">
    /// The list of promises generated by <paramref name="expansion" />
    /// will be stored/passed onto here.
    /// </param>
    /// <param name="promiseId">
    /// The promise ID for the macro job, needed to unregister
    /// it from <paramref name="jobScheduling" /> when the macro
    /// job has finished expanding.
    /// </param>
    /// <param name="expansion">
    /// User-supplied generator that lists out
    /// the promise objects and work descriptions for
    /// the micro jobs.
    /// </param>
    public MacroJob(JobSchedulingSystem jobScheduling,
                    IPromiseListBuilder resultBuilder,
                    PromiseId promiseId,
                    MacroJobExpansion expansion)
    {
        JobScheduling = jobScheduling;
        ResultBuilder = resultBuilder;
        PromiseId = promiseId;
        Expansion = expansion;

        _count = 1;
    }

    internal bool Reserve()
    {
        lock (this)
        {
            var count = _count;
            if (count <= 0)
                return false;
            _count = ++count;
            return true;
        }
    }

    internal void AddChild(MacroJobMessage child)
    {
        lock (this)
        {
            MacroJobLinks.Append(child, ref _children);
        }
    }

    internal bool RemoveChild(MacroJobMessage? child)
    {
        bool dead;

        lock (this)
        {
            int remaining = --_count;
            dead = (remaining <= 0);
            if (child is not null)
                MacroJobLinks.Remove(child, ref _children);
        }

        if (dead)
            JobScheduling.UnregisterMacroJob(PromiseId);

        return dead;
    }
}
