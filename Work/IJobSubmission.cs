using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace JobBank.Work
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
        ValueTask<RunJobReplyMessage> RunJobAsync(
            RunJobRequestMessage request,
            CancellationToken cancellationToken);
    }

    [MessagePackObject]
    public struct RunJobRequestMessage
    {
        [Key("r")]
        public string? Route { get; set; }

        [Key("t")]
        public string ContentType { get; set; }

        [Key("d")]
        public ReadOnlySequence<byte> Data { get; set; }
    }

    [MessagePackObject]
    public struct RunJobReplyMessage
    {
        [Key("t")]
        public string ContentType { get; set; }

        [Key("d")]
        public ReadOnlySequence<byte> Data { get; set; }
    }

}
