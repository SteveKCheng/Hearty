using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JobBank.Scheduling;

namespace JobBank.Server.Program
{
    /// <summary>
    /// Triggers actions after short, relative timeouts in a scalable way.
    /// </summary>
    /// <remarks>
    /// Timeouts are bucketed to simplify the implementation, as
    /// well as to force consolidation of timeout thresholds.
    /// So this class is suitable for implementing refreshing of 
    /// user interfaces, but for not any (long-term) expiry date/times 
    /// that are supposed to be user-customizable.
    /// </remarks>
    public abstract class TimeoutProvider
    {
        /// <summary>
        /// Register an action to execute after an approximate timeout.
        /// </summary>
        /// <param name="bucket">
        /// When the action is desired to be executed, relative to the current
        /// time.  
        /// </param>
        /// <param name="action">
        /// The action to execute.
        /// </param>
        /// <param name="state">
        /// Arbitrary state to pass into <paramref name="action" />.
        /// </param>
        public abstract void Register(TimeoutBucket bucket, 
                                      ExpiryAction action, 
                                      object? state);

        /// <summary>
        /// Get the number of milliseconds corresponding to a timeout bucket.
        /// </summary>
        public static int GetMillisecondsForBucket(TimeoutBucket bucket)
            => bucket switch
            {
                TimeoutBucket.After200Milliseconds => 200,
                TimeoutBucket.After1Second => 1000,
                TimeoutBucket.After5Seconds => 5000,
                TimeoutBucket.After30Seconds => 30000,
                _ => throw new ArgumentOutOfRangeException(nameof(bucket))
            };

        /// <summary>
        /// Number of buckets for timeouts, which are statically defined.
        /// </summary>
        public readonly int NumberOfBuckets = 4;
    }

    /// <summary>
    /// A discrete set of timeout lengths accepted by <see cref="TimeoutProvider" />.
    /// </summary>
    public enum TimeoutBucket
    {
        /// <summary>
        /// Trigger after approximately 200 milliseconds.
        /// </summary>
        After200Milliseconds,

        /// <summary>
        /// Trigger after approximately 1 second.
        /// </summary>
        After1Second,

        /// <summary>
        /// Trigger after approximately 5 seconds.
        /// </summary>
        After5Seconds,

        /// <summary>
        /// Trigger after approximately 5 seconds.
        /// </summary>
        After30Seconds
    }
}
