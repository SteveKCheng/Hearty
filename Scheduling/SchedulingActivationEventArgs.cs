using System;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Describes a flow that is being activated or de-activating
    /// from its parent scheduling group.
    /// </summary>
    public struct SchedulingActivationEventArgs
    {
        /// <summary>
        /// Whether the child flow if being activated or de-activated.
        /// </summary>
        public bool Activated { get; }

        /// <summary>
        /// Counts how many activation or de-activation
        /// events have occurred so far including this one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This number is a version counter that orders the activation
        /// and de-activation events.  In a concurrent setting, the
        /// event handler is not guaranteed to be invoked in sequence
        /// in the same order that the events occur.
        /// </para>
        /// <para>
        /// This number will silently wrap around when the
        /// count exceeds <see cref="uint.MaxValue" />.
        /// </para>
        /// </remarks>
        public uint Counter { get; }

        /// <summary>
        /// The attached object that was passed to 
        /// <see cref="SchedulingGroup{T}.AdmitChild" />
        /// for the flow being activated or de-activated.
        /// </summary>
        public object? Attachment { get; }

        internal SchedulingActivationEventArgs(bool activated, 
                                               uint counter, 
                                               object? attachment)
        {
            Activated = activated;
            Counter = counter;
            Attachment = attachment;
        }
    }
}
