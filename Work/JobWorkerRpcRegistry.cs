using System;
using Hearty.Carp;

namespace Hearty.Work;

/// <summary>
/// Registry for the worker side of the RPC protocol for distributing job requests.
/// </summary>
/// <remarks>
/// An instance of this class can be modified to support custom
/// functions over the same RPC connection used to communicate
/// with the job worker.  <see cref="WorkerHost" /> 
/// requires this sub-class of <see cref="RpcRegistry" />, 
/// and not the base class, so that
/// the constructor of this class can enforce the minimum
/// requirements for the job-serving protocol.
/// </remarks>
public class JobWorkerRpcRegistry : RpcRegistry
{
    /// <summary>
    /// Construct with the basic minimum settings.
    /// </summary>
    public JobWorkerRpcRegistry()
        : base(new RpcExceptionSerializer(WorkerHost.SerializeOptions), WorkerHost.SerializeOptions)
    {
        this.Add<JobRequestMessage, JobReplyMessage>(
            WorkerHost.TypeCode_RunJob, WorkerHost.RunJobImplAsync);

        this.Add<PingMessage, PongMessage>(
            WorkerHost.TypeCode_Heartbeat, WorkerHost.CheckHealthAsync);
    }

    internal static readonly JobWorkerRpcRegistry DefaultInstance = new JobWorkerRpcRegistry();
}
