using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Extension methods dealing with cancellation.
    /// </summary>
    public static class CancellationExtensions
    {
        /// <summary>
        /// Triggers cancellation as a background task, so that
        /// callbacks are not synchronously executed.
        /// </summary>
        /// <param name="source">
        /// The cancellation source to trigger.
        /// </param>
        /// <remarks>
        /// This method helps to lower the latency of processing
        /// cancellations in server applications, when the amount of
        /// code that have been registered as callbacks to the cancellation
        /// token cannot be (statically) bounded.
        /// </remarks>
        public static void CancelInBackground(this CancellationTokenSource source)
        {
            Task.Factory.StartNew(o => Unsafe.As<CancellationTokenSource>(o!).Cancel(),
                                  source);
        }

        /// <summary>
        /// Triggers cancellation, as a background task depending
        /// on a run-time flag.
        /// </summary>
        /// <param name="source">
        /// The cancellation source to trigger.
        /// </param>
        /// <param name="background">
        /// Whether the cancellation should be in the background.
        /// </param>
        public static void CancelMaybeInBackground(this CancellationTokenSource source,
                                                   bool background)
        {
            if (background)
                source.CancelInBackground();
            else
                source.Cancel();
        }
    }
}
