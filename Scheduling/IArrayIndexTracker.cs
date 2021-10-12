using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Receives updates to the indices to elements in an array.
    /// </summary>
    internal interface IArrayIndexTracker<TValue>
    {
        /// <summary>
        /// Called when an element in the array moves and is assigned
        /// a new index.
        /// </summary>
        /// <param name="element">Reference to the element in the array. </param>
        /// <param name="newIndex">The new index being assigned. </param>
        void ChangeIndex(ref TValue element, int newIndex);
    }
}
