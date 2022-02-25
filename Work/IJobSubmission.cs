using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace Hearty.Work
{
    /// <summary>
    /// Interface to execute jobs, suitable to expose as 
    /// remote procedure calls (RPC) over a network.
    /// </summary>
    public interface IJobSubmission
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
}
