using Hearty.Scheduling;
using Hearty.Carp;
using Hearty.Work;
using System;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Utilities;

namespace Hearty.Server
{
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
        }
    }
}
