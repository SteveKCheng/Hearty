using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace Hearty.Work;

/// <summary>
/// Interface to execute jobs, suitable to expose as 
/// remote procedure calls (RPC) over a network.
/// </summary>
/// <remarks>
/// <see cref="IAsyncDisposable" /> is part of this interface
/// as many implementations are expected to be heavy "services"
/// that want explicit disposal.
/// </remarks>
public interface IJobSubmission : IAsyncDisposable
{
    /// <summary>
    /// Execute one job asynchronously, 
    /// possibly in parallel with other jobs.
    /// </summary>
    /// <param name="request">
    /// Serializable inputs describing the job to execute.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the execution of the job if triggered.
    /// </param>
    /// <returns>
    /// Asynchronous task that completes with the results
    /// from the executed job.
    /// </returns>
    ValueTask<JobReplyMessage> RunJobAsync(
        JobRequestMessage request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called when the job server (or an application assuming
    /// that role) wants to check that the worker is alive
    /// and ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is intended to be called periodically by the
    /// job server when the job server and the worker live in different processes.
    /// The server will then be able to detect when the worker or
    /// its network connection to it goes down.  
    /// </para>
    /// <para>
    /// In many cases, the worker going down can be detected just from
    /// the network connection failing or closing.  For instance, if
    /// the user-space process for the worker crashes, the operating system
    /// kernel would close all active connections (assuming the kernel
    /// is managing the core connection state).  
    /// </para>
    /// <para>
    /// In other cases, a network connection being down can only be
    /// detected should the server to send something
    /// "along the wire" before a lower-level protocol such as TCP
    /// kicks in and detects the failure (typically after some set
    /// number of retry attempts).  
    /// </para>
    /// <para>
    /// The "heartbeat" message from
    /// the job server is what will be used to trigger the test
    /// transmission.  Some lower-level protocols such as TCP
    /// and WebSockets have their own "heartbeat" messages, but
    /// Hearty's RPC protocol for job submission provisions its own
    /// heartbeats, for several reasons.  Firstly, application-level code 
    /// may not be able to control low-level heartbeats precisely.  
    /// Or, when testing over more
    /// primitive communication channels such as in-memory streams,
    /// there is no lower-level mechanism for heartbeats in the
    /// first place.  
    /// </para>
    /// <para>
    /// Furthermore, there are some kinds of 
    /// application-level failures, such as deadlocks, where the
    /// process appears fine from the perspective of the operating
    /// system kernel, but the worker has obviously become disabled.
    /// This method may be instrumented to do application-level
    /// checks that the worker is "healthy".
    /// </para>
    /// <para>
    /// The default implementation does nothing.
    /// </para>
    /// <para>
    /// The job server shall shut down the RPC connection if this
    /// method fails (throws an exception).
    /// </para>
    /// <para>
    /// The worker will necessarily be testing from the other end
    /// of the network connection when it attempts to transmit its reply
    /// to this "health check".  If the reply cannot be sent, the worker
    /// should assume the network connection or the job server has gone
    /// down, and either restart the connection or exit.
    /// </para>
    /// </remarks>
    /// <param name="request">The heartbeat request message from
    /// the job server. </param>
    /// <param name="cancellationToken">
    /// Cancels the need for acknowledgement of the heartbeat, if triggered.
    /// </param>
    /// <returns>
    /// The "pong" message (reply to the heartbeat) to send back
    /// to the job server.  Producing any message implies success;
    /// failure should be indicated by throwing an exception
    /// from this method.
    /// </returns>
    ValueTask<PongMessage> CheckHealthAsync(
        PingMessage request,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new PongMessage());
    }
}

/// <summary>
/// A message sent by the job distribution system
/// to a worker host to request it to run a job.
/// </summary>
/// <remarks>
/// Messages of this type are sent using a MessagePack-based
/// RPC protocol.  To reduce transmission overhead,
/// keys are single characters.
/// </remarks>
[MessagePackObject]
public struct JobRequestMessage
{
    /// <summary>
    /// A string that may be used to select a sub-function 
    /// to apply to the data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property is essentially a command string.  It may
    /// </para>
    /// </remarks>
    [Key("r")]
    public string? Route { get; set; }

    /// <summary>
    /// The IANA media type to interpret <see cref="Data" /> as.
    /// </summary>
    [Key("t")]
    public string ContentType { get; set; }

    /// <summary>
    /// The amount of time the job would take, in milliseconds,
    /// as estimated by the requestor.
    /// </summary>
    [Key("w")]
    public int EstimatedWait { get; set; }

    /// <summary>
    /// The ID of this job which should be unique for the
    /// lifetime of the worker.
    /// </summary>
    /// <remarks>
    /// This property is mainly useful for logging.
    /// </remarks>
    [Key("#")]
    public uint ExecutionId { get; set; }

    /// <summary>
    /// Contains application-level request input, 
    /// as a raw sequence of bytes.
    /// </summary>
    [Key("d")]
    public ReadOnlySequence<byte> Data { get; set; }
}

/// <summary>
/// The reply message sent by a worker host when
/// it completes running a requested job.
/// </summary>
/// <remarks>
/// Messages of this type are sent using a MessagePack-based
/// RPC protocol.  To reduce transmission overhead,
/// keys are single characters.
/// </remarks>
[MessagePackObject]
public struct JobReplyMessage
{
    /// <summary>
    /// The IANA media type to interpret <see cref="Data" /> as.
    /// </summary>
    [Key("t")]
    public string ContentType { get; set; }

    /// <summary>
    /// Contains application-level output for the job, 
    /// as a raw sequence of bytes.
    /// </summary>
    [Key("d")]
    public ReadOnlySequence<byte> Data { get; set; }
}
