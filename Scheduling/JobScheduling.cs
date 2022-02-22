using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Generic algorithms for scheduling jobs.
    /// </summary>
    public static class JobScheduling
    {
        /// <summary>
        /// Assign jobs from a channel as soon as resources to execute that
        /// job are made available from another channel.
        /// </summary>
        /// <typeparam name="TInput">The type of input for the jobs. </typeparam>
        /// <typeparam name="TOutput">The type of output for the jobs. </typeparam>
        /// <param name="jobsChannel">
        /// Presents the jobs to execute in a potentially non-ending sequence.
        /// The channel may be implemented by a queuing system like the one
        /// from this library.
        /// </param>
        /// <param name="vacanciesChannel">
        /// Presents claims to resources to execute the jobs in a potentially
        /// non-ending sequence.  This channel may also be implemented by a queuing system 
        /// like the one from this library.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to stop processing jobs.
        /// </param>
        /// <returns>
        /// Asynchronous task that completes when interrupted by 
        /// <paramref name="cancellationToken" />, or when at least one
        /// of the channels is closed.
        /// </returns>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> signals cancellation.
        /// </exception>
        public static async Task RunJobsAsync<TInput, TOutput>(
            ChannelReader<ILaunchableJob<TInput, TOutput>> jobsChannel,
            ChannelReader<JobVacancy<TInput, TOutput>> vacanciesChannel,
            CancellationToken cancellationToken)
        {
            while (await vacanciesChannel.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await jobsChannel.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    break;

                if (vacanciesChannel.TryRead(out var vacancy))
                {
                    if (jobsChannel.TryRead(out var job))
                        vacancy.TryLaunchJob(job);
                    else
                        vacancy.Dispose();
                }
            }
        }
    }
}
