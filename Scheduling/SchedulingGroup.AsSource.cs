using System;

namespace JobBank.Scheduling
{
    public partial class SchedulingGroup<TJob>
    {
        /// <summary>
        /// Adapts <see cref="SchedulingGroup{TJob}" /> to
        /// <see cref="SchedulingUnit{TJob}" />.
        /// </summary>
        private class SourceImpl : SchedulingUnit<TJob>
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
                subgroup.OnFirstActivated += (sender, e) => Activate();
            }

            /// <inheritdoc />
            protected override TJob? TakeJob(out int charge)
                => _subgroup.TakeJob(out charge);
        }

        /// <summary>
        /// Adapt this instance as a <see cref="SchedulingUnit{TJob}" />
        /// so that it can participate in a hierarchy of queues.
        /// </summary>
        /// <param name="parent">
        /// The parent scheduling group that this sub-group shall 
        /// be part of.
        /// </param>
        /// <param name="weight">
        /// The weight assigned to this sub-group within the
        /// its new parent scheduling group.
        /// </param>
        /// <returns>
        /// An instance of <see cref="SchedulingUnit{TJob}" />
        /// that forwards the messages from the child queues
        /// that this instance manages.
        /// </returns>
        protected SchedulingUnit<TJob> AsSource(SchedulingGroup<TJob> parent,
                                                int weight)
            => new SourceImpl(parent, this, weight);
    }
}
