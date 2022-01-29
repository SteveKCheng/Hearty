using JobBank.Scheduling;
using JobBank.Work;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
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
    public class LocalWorkerAdaptor : IJobWorker<PromiseJob, PromiseData>
    {
        public string Name { get; }

        public bool IsAlive => true;

        public event EventHandler<WorkerEventArgs>? OnEvent;

        public void AbandonJob(uint executionId)
        {
        }

        public ValueTask<PromiseData> ExecuteJobAsync(uint executionId,
                                                      IRunningJob<PromiseJob> runningJob,
                                                      CancellationToken cancellationToken)
            => RemoteWorkerProxy.ForwardExecuteJobAsync(_impl, 
                                                        executionId, 
                                                        runningJob, 
                                                        cancellationToken);
        
        private readonly IJobSubmission _impl;

        public LocalWorkerAdaptor(IJobSubmission impl, string name)
        {
            _impl = impl;
            Name = name;
        }
    }
}
