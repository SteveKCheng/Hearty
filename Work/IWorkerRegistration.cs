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

    [MessagePackObject]
    public struct RegisterWorkerRequestMessage
    {
        [Key("n")]
        public string Name { get; set; }

        [Key("c")]
        public ushort Concurrency { get; set; }
    }

    public enum RegisterWorkerReplyStatus
    {
        Ok = 0,
        NameAlreadyExists = 1,
        ConcurrentRegistration = 2,
    }

    [MessagePackObject]
    public struct RegisterWorkerReplyMessage
    {
        [Key("s")]
        public RegisterWorkerReplyStatus Status { get; set; }
    }
}
