using Hearty.Scheduling;
using Hearty.Work;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Adapts <see cref="IJobSubmission" /> to get called locally
/// as a job worker.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IJobSubmission" /> is the interface for remote
/// job submission, while <see cref="IJobWorker{TInput, TOutput}" />
/// is the in-process interface.  A true local worker should
/// implement <see cref="IJobWorker{TInput, TOutput}" /> directly
/// as that interface allows passing in .NET objects as references, 
/// without serialization.  
/// </para>
/// <para>
/// But, this adaptor class can be used to locally test 
/// implementations of job workers that are normally 
/// intended to be run in remote hosts.
/// </para>
/// </remarks>
public class LocalWorkerAdaptor : IJobWorker<PromisedWork, PromiseData>
{
    /// <inheritdoc cref="IWorkerNotification.Name" />
    public string Name { get; }

    /// <inheritdoc cref="IWorkerNotification.IsAlive" />
    public bool IsAlive => true;

    /// <inheritdoc cref="IWorkerNotification.OnEvent" />
    public event EventHandler<WorkerEventArgs>? OnEvent;

    /// <inheritdoc cref="IJobWorker{TInput, TOutput}.AbandonJob" />
    public void AbandonJob(uint executionId)
    {
    }

    /// <inheritdoc cref="IJobWorker{TInput, TOutput}.ExecuteJobAsync" />
    public ValueTask<PromiseData> ExecuteJobAsync(uint executionId,
                                                  IRunningJob<PromisedWork> runningJob,
                                                  CancellationToken cancellationToken)
        => RemoteWorkerProxy.ForwardExecuteJobAsync(_impl,
                                                    executionId,
                                                    runningJob,
                                                    cancellationToken,
                                                    _cancellationSource.Token);

    /// <summary>
    /// Triggered to stop all jobs on disposal.
    /// </summary>
    private readonly CancellationTokenSource _cancellationSource = new();

    /// <summary>
    /// Cancel all pending requests and tear down the worker.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Only emit the event the first time this method is invoked.
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            try
            {
                OnEvent?.Invoke(this, new WorkerEventArgs
                {
                    Kind = WorkerEventKind.Shutdown
                });
            }
            catch 
            { 
            }

            _cancellationSource.Cancel();

            await _impl.DisposeAsync().ConfigureAwait(false);

            GC.SuppressFinalize(this);
        }
    }

    private int _isDisposed;

    private readonly IJobSubmission _impl;

    /// <summary>
    /// Constructs a local worker that executes jobs 
    /// according to <see cref="IJobSubmission" />.
    /// </summary>
    /// <param name="impl">
    /// Implementation of executing the work using serialized inputs/outputs.
    /// </param>
    /// <param name="name">The name of the new worker, for logging
    /// and reporting. </param>
    public LocalWorkerAdaptor(IJobSubmission impl, string name)
    {
        _impl = impl;
        Name = name;
    }
}
