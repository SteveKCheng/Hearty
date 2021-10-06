using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// A long-lived identifier for a promise that can refer
    /// to it even if the <see cref="Promise" /> object
    /// has been swapped out of memory.
    /// </summary>
    /// <remarks>
    /// This is a 64-bit integer split into two parts:
    /// the upper 32-bit word is generated for each instance
    /// of the promise service, and the lower 32-bit word
    /// is a sequence number.  The upper 32-bit word lets multiple
    /// service instances synchronize promises with each other 
    /// when recovering from failures.  Using 128-bit GUIDs for
    /// promise IDs would be ultra-safe, but is not done for the
    /// sake of conserving memory.
    /// </remarks>
    public readonly struct PromiseId
    {
        /// <summary>
        /// The 64-bit integer representing the promise ID.
        /// </summary>
        private readonly ulong _number;

        /// <summary>
        /// Construct the promise ID from its constituent parts.
        /// </summary>
        public PromiseId(uint serviceId, uint sequenceNumber)
        {
            _number = (((ulong)serviceId) << 32) | sequenceNumber;
        }

        /// <summary>
        /// Construct the promise ID from the raw 64-bit integer.
        /// </summary>
        public PromiseId(ulong number)
        {
            _number = number;
        }

        /// <summary>
        /// The ID assigned to the service instance that
        /// created the promise.
        /// </summary>
        public uint ServiceId => (uint)(_number >> 32);

        /// <summary>
        /// The sequence number for the promise generated
        /// by the service instance.
        /// </summary>
        public uint SequenceNumber => (uint)(_number & 0xFFFFFFFF);
    }
}
