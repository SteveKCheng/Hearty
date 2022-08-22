using Hearty.Scheduling;
using Hearty.Carp;
using Hearty.Work;
using System;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Utilities;
using Microsoft.Extensions.Logging;

namespace Hearty.Server;

/// <summary>
/// Submits promise-backed jobs to a remote worker
/// for distributed job scheduling.
/// </summary>
/// <remarks>
/// This class proxies for a remote worker in the distributed job scheduling.
/// The job inputs are serialized and sent to the remote host for
/// processing, over an asynchronous RPC protocol.
/// </remarks>
internal sealed class RemoteWorkerProxy : IJobWorker<PromisedWork, PromiseData>
{
    /// <inheritdoc cref="IWorkerNotification.Name" />
    public string Name { get; }

    /// <inheritdoc cref="IWorkerNotification.IsAlive" />
    public bool IsAlive => !_rpc.IsClosingStarted;

    /// <inheritdoc cref="IWorkerNotification.OnEvent" />
    public event EventHandler<WorkerEventArgs>? OnEvent;

    void IJobWorker<PromisedWork, PromiseData>.AbandonJob(uint executionId)
    {
    }

    /// <summary>
    /// Translates an invocation of
    /// <see cref="IJobWorker{PromiseJob, PromiseData}.ExecuteJobAsync"/>
    /// into an invocation of <see cref="IJobSubmission.RunJobAsync" />.
    /// </summary>
    /// <remarks>
    /// This function is factored out so both <see cref="RemoteWorkerProxy" />
    /// and <see cref="LocalWorkerAdaptor" /> can use it.
    /// </remarks>
    /// <typeparam name="TImpl">
    /// Type that implements <see cref="IJobSubmission" />.
    /// This may be a wrapper structure to force the .NET compiler 
    /// to monomorphize the code, i.e. to avoid one layer of
    /// unnecessary virtual dispatch.
    /// </typeparam>
    /// <param name="impl">
    /// The instance implementing <see cref="IJobSubmission" />
    /// to forward the call to.
    /// </param>
    /// <param name="executionId">
    /// An arbitrary integer, assigned by some convention, that may 
    /// distinguish the jobs executed by this worker.
    /// </param>
    /// <param name="runningJob">
    /// Holds an object that manages the job,
    /// and contains the inputs to be serialized
    /// for <see cref="IJobSubmission.RunJobAsync" />.
    /// </param>
    /// <param name="jobCancellationToken">
    /// Cancellation token specific to the job, that may
    /// get triggered from the user.
    /// </param>
    /// <param name="workerCancellationToken">
    /// Cancellation token for the worker, that may be
    /// triggered when the worker is forcibly stopped.
    /// </param>
    /// <returns>
    /// Asynchronous task that completes with the output from 
    /// <see cref="IJobSubmission.RunJobAsync" />, after de-serialization.
    /// </returns>
    internal static async ValueTask<PromiseData> 
        ForwardExecuteJobAsync<TImpl>(TImpl impl,
                               uint executionId,
                               IRunningJob<PromisedWork> runningJob,
                               CancellationToken jobCancellationToken,
                               CancellationToken workerCancellationToken = default)
            where TImpl : IJobSubmission
    {
        CancellationToken cancellationToken;
        CancellationSourcePool.Use combinedCancellation = default;
        CancellationTokenRegistration jobCancellationReg = default;
        CancellationTokenRegistration workerCancellationReg = default;
        var exceptionTranslator = runningJob.Input.ExceptionTranslator;

        try
        {
            if (workerCancellationToken.CanBeCanceled && jobCancellationToken.CanBeCanceled)
            {
                combinedCancellation = CancellationSourcePool.Rent();
                jobCancellationReg = combinedCancellation.Source!.LinkOtherToken(jobCancellationToken);
                workerCancellationReg = combinedCancellation.Source!.LinkOtherToken(workerCancellationToken);
                cancellationToken = combinedCancellation.Token;
            }
            else
            {
                cancellationToken = workerCancellationToken.CanBeCanceled
                                        ? workerCancellationToken
                                        : jobCancellationToken;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var input = await runningJob.Input
                                        .InputSerializer(runningJob.Input.Data)
                                        .ConfigureAwait(false);

            var reply = await impl.RunJobAsync(new JobRequestMessage
            {
                Route = runningJob.Input.Route,
                ContentType = input.ContentType,
                EstimatedWait = runningJob.EstimatedWait,
                ExecutionId = executionId,
                Data = input.Body,
            }, cancellationToken).ConfigureAwait(false);

            var output = await runningJob.Input
                                         .OutputDeserializer
                                         .Invoke(runningJob.Input.Data,
                                                 new(reply.ContentType,
                                                     reply.Data))
                                         .ConfigureAwait(false);
            return output;
        }
        catch (Exception e) when (exceptionTranslator is not null)
        {
            return await exceptionTranslator.Invoke(runningJob.Input.Data,
                                                    runningJob.Input.PromiseId,
                                                    e)
                                            .ConfigureAwait(false);
        }
        finally
        {
            jobCancellationReg.Dispose();
            workerCancellationReg.Dispose();
            combinedCancellation.Dispose();
        }
    }

    ValueTask<PromiseData>
        IJobWorker<PromisedWork, PromiseData>.ExecuteJobAsync(
            uint executionId,
            IRunningJob<PromisedWork> runningJob,
            CancellationToken cancellationToken)
        => ForwardExecuteJobAsync(new JobSubmissionForwarder(this), 
                                  executionId, 
                                  runningJob, 
                                  cancellationToken);

    /// <summary>
    /// Shuts down the RPC connection.
    /// </summary>
    /// <remarks>
    /// The other side of the connection is assumed to cancel
    /// any jobs it is still running.  
    /// The C# implementation in Hearty, 
    /// <see cref="WorkerHost" />, does so.
    /// </remarks>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        // Send shutdown event as soon as this method is called,
        // without waiting for the RPC connection to close.
        SendShutdownEvent(WorkerEventKind.Shutdown);

        return _rpc.DisposeAsync();
    }

    private int _hasSentShutdownEvent;

    /// <summary>
    /// Emit the event for this worker shutting down, but
    /// only for the first time this method is called.
    /// </summary>
    private void SendShutdownEvent(WorkerEventKind eventKind)
    {
        if (Interlocked.Exchange(ref _hasSentShutdownEvent, 1) == 0)
        {
            // Clean up heartbeat timer
            var heartbeatTimer = Interlocked.Exchange(ref _heartbeatTimer, null);
            heartbeatTimer?.Dispose();

            try
            {
                OnEvent?.Invoke(this, new WorkerEventArgs
                {
                    Kind = eventKind
                });
            }
            catch (Exception e)
            {
                _logger.LogCritical(
                    e, 
                    "An exception occurred in invoking handlers for events from remote worker {worker}. ",
                    Name);
            }
        }
    }

    public RemoteWorkerProxy(string name, RpcConnection rpc, ILogger logger)
    {
        Name = name;
        _rpc = rpc;
        _logger = logger;

        rpc.OnClose += (o, e) => SendShutdownEvent(WorkerEventKind.Shutdown);
    }

    private readonly RpcConnection _rpc;
    private readonly ILogger _logger;

    /// <summary>
    /// Wrapper over <see cref="RemoteWorkerProxy" /> to expose,
    /// only internally, its implementation of <see cref="IJobSubmission" />.
    /// </summary>
    private readonly struct JobSubmissionForwarder : IJobSubmission
    {
        private readonly RemoteWorkerProxy _proxy;

        public JobSubmissionForwarder(RemoteWorkerProxy proxy)
            => _proxy = proxy;

        /// <inheritdoc cref="IJobSubmission.RunJobAsync" />
        public ValueTask<JobReplyMessage>
            RunJobAsync(JobRequestMessage request,
                        CancellationToken cancellationToken)
        {
            return _proxy._rpc.InvokeRemotelyAsync<JobRequestMessage,
                                                   JobReplyMessage>(
                WorkerHost.TypeCode_RunJob, request, cancellationToken);
        }

        ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
    }

    #region Heartbeats

    /// <summary>
    /// Start periodically sending "ping" messages to the worker
    /// and expect the "pong" messages back.
    /// </summary>
    /// <remarks>
    /// This method may only be called once for each instance.
    /// It should be called after the worker has registered itself.
    /// </remarks>
    /// <param name="period">
    /// The period of time between sending the heartbeat ping messages.
    /// </param>
    /// <param name="timeout">
    /// The period of time allowed for responses to the heartbeat ping
    /// message.  If the response does not come back within this time
    /// interval then the worker is considered unresponsive.
    /// </param>
    public void StartHeartbeats(TimeSpan period, TimeSpan timeout)
    {
        var heartbeatTimer = new Timer(
            s => ((RemoteWorkerProxy)s!).SendHeartbeatMessageAsync(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (Interlocked.CompareExchange(ref _heartbeatTimer, heartbeatTimer, null) != null)
        {
            heartbeatTimer.Dispose();
            throw new InvalidOperationException("Heartbeat timer has already started. ");
        }

        _heartbeatTimeout = timeout;
        heartbeatTimer.Change(period, period);
    }

    private async void SendHeartbeatMessageAsync()
    {
        if (Interlocked.Exchange(ref _heartbeatInProgress, 1) != 0)
            return;

        try
        {
            var cancellationSource = _heartbeatCancellation ?? new CancellationTokenSource();
            cancellationSource.CancelAfter(_heartbeatTimeout);

            try
            {
                await _rpc.InvokeRemotelyAsync<PingMessage, PongMessage>(
                    WorkerHost.TypeCode_Heartbeat,
                    new PingMessage(),
                    cancellationSource.Token).ConfigureAwait(false);
            }
            catch
            {
                if (!_rpc.IsClosingStarted)
                    return;

                _logger.LogError("Worker {worker} has become unresponsive.  Its connection will be terminated. ",
                                 Name);
                SendShutdownEvent(WorkerEventKind.Unresponsive);
                await _rpc.DisposeAsync().ConfigureAwait(false);
                return;
            }

            // Try to re-use the cancellation source for the next heartbeat
            _heartbeatCancellation = cancellationSource.TryReset() ? cancellationSource 
                                                                   : null;

            Volatile.Write(ref _heartbeatInProgress, 0);
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, 
                "An unexpected exception while trying to send a heartbeat message to worker {worker}. ",
                Name);
        }
    }

    /// <summary>
    /// Periodic timer which triggers sending of heartbeat ping messages.
    /// </summary>
    private Timer? _heartbeatTimer;

    /// <summary>
    /// The time interval allowed for the worker to reply to the ping message.
    /// </summary>
    private TimeSpan _heartbeatTimeout;

    /// <summary>
    /// Cached cancellation source to trigger timeouts on
    /// the worker's response to the heartbeat ping message.
    /// </summary>
    private CancellationTokenSource? _heartbeatCancellation;

    /// <summary>
    /// Set to 1 when a heartbeat is being processed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This flag guards against multiple heartbeats from being sent concurrently.
    /// Without this flag, if one heartbeat pong message is slow to come back,
    /// the next heartbeat may still fire from the timer.
    /// </para>
    /// <para>
    /// This flag is also left "stuck" at 1 if a heartbeat fails, and the
    /// connection is about to be closed down.
    /// </para>
    /// </remarks>
    private int _heartbeatInProgress;

    #endregion
}
