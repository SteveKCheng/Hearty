using System;

namespace JobBank.Server.Program
{
    internal class SimpleQueueOwner : IJobQueueOwner
    {
        /// <inheritdoc />
        public string Title { get; }

        public SimpleQueueOwner(string title) => Title = title;

        /// <inheritdoc cref="IEquatable{T}.Equals(T?)" />
        public bool Equals(IJobQueueOwner? other)
            => other is SimpleQueueOwner s && Title.Equals(s.Title);

        /// <inheritdoc />
        public override string ToString() => Title;

        /// <inheritdoc cref="IComparable{T}.CompareTo(T?)" />
        public int CompareTo(IJobQueueOwner? other)
        {
            if (other is SimpleQueueOwner s)
                return Title.CompareTo(s.Title);
            else
                return typeof(SimpleQueueOwner).FullName!.CompareTo(other?.GetType().FullName);
        }
    }
}
