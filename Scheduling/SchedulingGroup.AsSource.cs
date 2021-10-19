using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace JobBank.Scheduling
{
    public partial class SchedulingGroup<T>
    {
        /// <summary>
        /// Adapts <see cref="SchedulingGroup{T}" /> to
        /// <see cref="SchedulingUnit{T}" />.
        /// </summary>
        private sealed class SourceImpl : SchedulingUnit<T>
        {
            /// <summary>
            /// The subgroup of queues where messages will
            /// be forward from to this instance's parent.
            /// </summary>
            private readonly SchedulingGroup<T> _subgroup;

            /// <summary>
            /// Prepare to forward messages from a sub-group 
            /// of queues as if they came from a single queue. 
            /// </summary>
            public SourceImpl(SchedulingGroup<T> subgroup)
            {
                _subgroup = subgroup;
            }

            /// <inheritdoc />
            protected override bool TryTakeItem(
                [MaybeNullWhen(false)] out T item, out int charge)
                => _subgroup.TryTakeItem(out item, out charge);

            /// <summary>
            /// Called by <see cref="SchedulingGroup{T}" />
            /// when it activates from an empty state.
            /// </summary>
            internal void OnSubgroupActivated() => Activate();
        }

        /// <summary>
        /// Adapt this instance as a <see cref="SchedulingUnit{T}" />
        /// so that it can participate in a hierarchy of queues.
        /// </summary>
        /// <returns>
        /// An instance of <see cref="SchedulingUnit{T}" />
        /// that forwards the messages from the child queues
        /// that this instance manages.  There is only one instance
        /// even if multiple calls are made to this method, as it
        /// never makes sense to consume the same scheduling group
        /// from multiple clients.  
        /// </returns>
        protected SchedulingUnit<T> AsSource()
        {
            var toWakeUp = _toWakeUp;
            if (toWakeUp == null)
            {
                var newSourceAdaptor = new SourceImpl(this);
                toWakeUp = Interlocked.CompareExchange(ref _toWakeUp,
                                                       newSourceAdaptor,
                                                       null);
                if (toWakeUp == null)
                    return newSourceAdaptor;
            }

            if (toWakeUp is SourceImpl sourceAdaptor)
                return sourceAdaptor;

            throw new InvalidOperationException(
                "Cannot call AsSource when AsChannelReader had already been called. ");
        }

        /// <summary>
        /// Reference to the adaptor of this instance to be a scheduling
        /// source, needed to inform it when this instance re-activates.
        /// </summary>
        private object? _toWakeUp;
    }
}
