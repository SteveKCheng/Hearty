using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Channels;

namespace JobBank.Scheduling
{
    /// <summary>
    /// De-queues from sub-queues according to their priority class.
    /// </summary>
    /// <remarks>
    /// This class is used to implement prioritized fair scheduling.
    /// The server that wants to schedule its jobs this way
    /// creates a fixed set of "priority classes" that can be
    /// assigned weights.  Job items to be scheduled are queued
    /// into the sub-queue for that priority class.
    /// </remarks>
    /// <typeparam name="TMessage">
    /// The type of job or message being delivered by this queue system.
    /// </typeparam>
    /// <typeparam name="TQueue">
    /// The abstract queue instantiated for each priority class.
    /// </typeparam>
    public class PrioritizedQueueSystem<TMessage, TQueue> 
        : IReadOnlyList<TQueue>
        where TQueue : ISchedulingFlow<TMessage>
    {
        /// <summary>
        /// Prepare for fair scheduling on a fixed number of
        /// priority classes.
        /// </summary>
        /// <param name="capacity">
        /// The number of priority classes in this queue system.
        /// It cannot be changed after construction, as it is expected
        /// that a server would set up a fixed number of priority classes,
        /// though their weights can change dynamically. 
        /// </param>
        /// <param name="factory">
        /// Called to instantiate the abstract queue for each priority class.
        /// </param>
        public PrioritizedQueueSystem(int capacity, Func<TQueue> factory)
        {
            _schedulingGroup = new SchedulingGroup<TMessage>(capacity);

            var members = new TQueue[capacity];
            for (int i = 0; i < members.Length; ++i)
            {
                var member = factory();
                members[i] = member;
                _schedulingGroup.AdmitChild(member.AsFlow(), activate: false);
            }

            _members = members;
        }

        /// <summary>
        /// Implements fair scheduling between all the priority classes.
        /// </summary>
        private readonly SchedulingGroup<TMessage> _schedulingGroup;

        /// <summary>
        /// Change the weight for a priority class.
        /// </summary>
        /// <param name="priority">
        /// The priority class, indexed from 0 to the number of priority
        /// classes minus one.
        /// </param>
        /// <param name="weight">
        /// The desired weight for the priority class; must be between
        /// 1 and 128 (inclusive).
        /// </param>
        public void ResetWeight(int priority, int weight)
        {
            _schedulingGroup.ResetWeight(_members[priority].AsFlow(), 
                                         weight, 
                                         reset: false);
        }

        /// <summary>
        /// Iterates through all the priority classes.
        /// </summary>
        public IEnumerator<TQueue> GetEnumerator()
        {
            for (int i = 0; i < _members.Length; ++i)
                yield return _members[i];
        }

        /// <inheritdoc cref="IEnumerable.GetEnumerator" />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Holds the sub-queues (systems) for each priority class.
        /// </summary>
        private readonly TQueue[] _members;

        /// <summary>
        /// The total number of priority classes.
        /// </summary>
        public int Count => _members.Length;

        /// <summary>
        /// Get the object representing the priority class.
        /// </summary>
        /// <param name="index">
        /// The priority class, indexed from 0 to the number of priority
        /// classes minus one.
        /// </param>
        public TQueue this[int index] => _members[index];

        /// <summary>
        /// Obtain the reading side of the channel which receives
        /// the messages in prioritized order.
        /// </summary>
        public ChannelReader<TMessage> AsChannel() => _schedulingGroup.AsChannelReader();

        /// <summary>
        /// Stop receiving messages from the channel returned by
        /// <see cref="AsChannel" />.
        /// </summary>
        public void TerminateChannel() => _schedulingGroup.TerminateChannelReader();
    }
}
