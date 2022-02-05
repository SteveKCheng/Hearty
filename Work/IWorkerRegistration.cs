using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace JobBank.Work
{
    /// <summary>
    /// Interface to register a worker host to a job distribution
    /// system, suitable to expose as remote procedure calls (RPC),
    /// by the latter to the former.
    /// </summary>
    public interface IWorkerRegistration
    {
        /// <summary>
        /// Register the caller as a worker host.
        /// </summary>
        /// <param name="request">
        /// Serializable parameters describing the worker host.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancels this operation if triggered.
        /// </param>
        /// <returns>
        /// Asynchronous task that completes with the status of
        /// the registration.
        /// </returns>
        ValueTask<RegisterWorkerReplyMessage>
            RegisterWorkerAsync(RegisterWorkerRequestMessage request,
                                CancellationToken cancellationToken);
    }

    /// <summary>
    /// Message sent by a worker host to introduce and register 
    /// itself to the job distribution system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Messages of this type are sent using a MessagePack-based
    /// RPC protocol.  To reduce transmission overhead,
    /// keys are single characters.  
    /// </para>
    /// <para>
    /// Registration is always scoped to the RPC connection on
    /// which this message is sent, so that the worker is
    /// implicitly de-registered when the connection is torn
    /// down, whether co-operatively or abruptly (because
    /// the worker host may have died).  Automatic
    /// teardown functionality being so critical, the protocol
    /// completely disallows sharing the same connection
    /// for multiple worker hosts.
    /// </para>
    /// </remarks>
    [MessagePackObject]
    public struct RegisterWorkerRequestMessage
    {
        /// <summary>
        /// The name of the worker, typically its host name.
        /// </summary>
        [Key("n")]
        public string Name { get; set; }

        /// <summary>
        /// The degree of concurrency (number of parallel threads
        /// of execution) available in the worker.
        /// </summary>
        [Key("c")]
        public ushort Concurrency { get; set; }
    }

    /// <summary>
    /// The positive or negative ackowledgement to a worker
    /// host's request to register.
    /// </summary>
    public enum RegisterWorkerReplyStatus
    {
        /// <summary>
        /// The worker has been accepted into the job distribution system.
        /// </summary>
        Ok = 0,

        /// <summary>
        /// The name has been taken by another worker.
        /// </summary>
        NameAlreadyExists = 1,

        /// <summary>
        /// More than one registration is being attempted on the same
        /// connection.
        /// </summary>
        ConcurrentRegistration = 2,
    }

    /// <summary>
    /// Message sent by the job distribution system to 
    /// acknowledge the registration of a worker host.
    /// </summary>
    /// <remarks>
    /// Messages of this type are sent using a MessagePack-based
    /// RPC protocol.  To reduce transmission overhead,
    /// keys are single characters.  
    /// </remarks>
    [MessagePackObject]
    public struct RegisterWorkerReplyMessage
    {
        /// <summary>
        /// The job distribution's response to the request
        /// by a worker to register.
        /// </summary>
        [Key("s")]
        public RegisterWorkerReplyStatus Status { get; set; }
    }
}
