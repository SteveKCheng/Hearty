using JobBank.Scheduling;
using JobBank.WebSockets;
using JobBank.Work;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Submits promise-backed jobs to a remote worker
    /// for distributed job scheduling.
    /// </summary>
    /// <remarks>
    /// This class proxies for a remote worker in the distributed job scheduling
    /// implemented by <see cref="JobSchedulingSystem" />.  The job inputs
    /// are serialized and sent to the remote host for
    /// processing, over an asynchronous RPC protocol.
    /// </remarks>
    internal sealed class RemoteWorkerProxy : IJobWorker<PromiseJob, PromiseData>
                                            , IJobSubmission
    {
        /// <inheritdoc cref="IWorkerNotification.Name" />
        public string Name { get; }

        /// <inheritdoc cref="IWorkerNotification.IsAlive" />
        public bool IsAlive => !_rpc.IsClosingStarted;

        /// <inheritdoc cref="IWorkerNotification.OnEvent" />
        public event EventHandler<WorkerEventArgs>? OnEvent;

        void IJobWorker<PromiseJob, PromiseData>.AbandonJob(uint executionId)
        {
        }

        async ValueTask<PromiseData>
            IJobWorker<PromiseJob, PromiseData>.ExecuteJobAsync(
                uint executionId,
                IRunningJob<PromiseJob> runningJob,
                CancellationToken cancellationToken)
        {
            var contentType = runningJob.Input.Data.SuggestedContentType;

            var reply = await RunJobAsync(new RunJobRequestMessage
            {
                Route = runningJob.Input.Route,
                ContentType = contentType,
                InitialWait = runningJob.InitialWait,
                ExecutionId = executionId,
                Data = await runningJob.Input
                                       .Data
                                       .GetPayloadAsync(contentType, cancellationToken)
                                       .ConfigureAwait(false)
            }, cancellationToken).ConfigureAwait(false);

            var output = new Payload(reply.ContentType, reply.Data);
            return output;
        }

        public RemoteWorkerProxy(string name, RpcConnection rpc)
        {
            Name = name;
            _rpc = rpc;

            rpc.OnClose += (o, e) => OnEvent?.Invoke(this, new WorkerEventArgs
            {
                Kind = WorkerEventKind.Shutdown
            });
        }

        private readonly RpcConnection _rpc;

        /// <inheritdoc cref="IJobSubmission.RunJobAsync" />
        public ValueTask<RunJobReplyMessage> 
            RunJobAsync(RunJobRequestMessage request, 
                        CancellationToken cancellationToken)
        {
            return _rpc.InvokeRemotelyAsync<RunJobRequestMessage, 
                                            RunJobReplyMessage>(
                WorkerHost.TypeCode_RunJob, request, cancellationToken);
        }
    }
}
