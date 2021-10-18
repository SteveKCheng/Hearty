using System;
using System.Threading;

namespace JobBank.Scheduling
{
    public partial class SchedulingGroup<TJob>
    {
        /// <summary>
        /// Adapts <see cref="SchedulingGroup{TJob}" /> to
        /// <see cref="SchedulingUnit{TJob}" />.
        /// </summary>
        private sealed class SourceImpl : SchedulingUnit<TJob>
        {
            /// <summary>
            /// The subgroup of queues where messages will
            /// be forward from to this instance's parent.
            /// </summary>
            private readonly SchedulingGroup<TJob> _subgroup;

            /// <summary>
            /// Prepare to forward messages from a sub-group 
            /// of queues as if they came from a single queue. 
            /// </summary>
            public SourceImpl(SchedulingGroup<TJob> parent,
                              SchedulingGroup<TJob> subgroup,
                              int weight)
                : base(parent, weight)
            {
                _subgroup = subgroup;
            }

            /// <inheritdoc />
            protected override TJob? TakeJob(out int charge)
                => _subgroup.TakeJob(out charge);

            /// <summary>
            /// Called by <see cref="SchedulingGroup{TJob}" />
            /// when it activates from an empty state.
            /// </summary>
            internal void ActivateFromParent() => Activate();

            internal void ChangeToNewParent(SchedulingGroup<TJob>? parent)
                => ChangeParent(parent);
        }

        /// <summary>
        /// Adapt this instance as a <see cref="SchedulingUnit{TJob}" />
        /// so that it can participate in a hierarchy of queues.
        /// </summary>
        /// <param name="parent">
        /// The parent scheduling group that this sub-group shall 
        /// be part of.
        /// </param>
        /// <returns>
        /// An instance of <see cref="SchedulingUnit{TJob}" />
        /// that forwards the messages from the child queues
        /// that this instance manages.  There is only one instance
        /// even if multiple calls are made to this method, as it
        /// never makes sense to consume the same scheduling group
        /// from multiple clients.  The parent from the previous
        /// call to this method, if any, will be detached from
        /// the returned instance.
        /// </returns>
        protected SchedulingUnit<TJob> AsSource(SchedulingGroup<TJob> parent)
        {
            SourceImpl? sourceAdaptor = _sourceAdaptor;

            if (sourceAdaptor == null)
            {
                sourceAdaptor = new SourceImpl(parent, this, weight: 1);
                sourceAdaptor = Interlocked.CompareExchange(
                                    ref _sourceAdaptor,
                                    sourceAdaptor,
                                    null) ?? sourceAdaptor;
            }

            if (sourceAdaptor.Parent != parent)
                sourceAdaptor.ChangeToNewParent(parent);
                
            return sourceAdaptor;
        }

        /// <summary>
        /// Reference to the adaptor of this instance to be a scheduling
        /// source, needed to inform it when this instance re-activates.
        /// </summary>
        private SourceImpl? _sourceAdaptor;
    }
}
