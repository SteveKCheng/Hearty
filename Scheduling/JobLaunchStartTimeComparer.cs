using System.Collections.Generic;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Orders instances of <see cref="IRunningJob" />
    /// according to when they were launched.
    /// </summary>
    internal class JobLaunchStartTimeComparer : IComparer<IRunningJob>
    {
        /// <summary>
        /// If true, items are presented in ascending (chronological) order
        /// of their launch times; otherwise in descending 
        /// (reverse chronological) order.
        /// </summary>
        public bool Ascending { get; }

        public JobLaunchStartTimeComparer() : this(true) { }

        public JobLaunchStartTimeComparer(bool ascending) 
            => Ascending = ascending;

        /// <inheritdoc cref="IComparer{T}.Compare" />
        public int Compare(IRunningJob? x, IRunningJob? y)
        {
            long xs = x?.LaunchStartTime ?? long.MinValue;
            long ys = y?.LaunchStartTime ?? long.MinValue;
            int r = xs.CompareTo(ys);
            return Ascending ? r : -r;
        }
    }
}
