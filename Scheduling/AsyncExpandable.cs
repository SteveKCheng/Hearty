using System;
using System.Collections.Generic;

namespace Hearty.Scheduling
{
    /// <summary>
    /// The discriminated union of either one value, or a multitude
    /// of values that are to be asynchronously produced.
    /// </summary>
    /// <typeparam name="T">The type of one value.
    /// </typeparam>
    /// <remarks>
    /// This type supports the dynamic expansion of sub-sequences
    /// of items in <see cref="SchedulingQueue{T}" />.
    /// </remarks>
    public readonly struct AsyncExpandable<T>
    {
        /// <summary>
        /// Holds a single value, if <see cref="Multiple" />
        /// is not null.
        /// </summary>
        /// <remarks>
        /// If <see cref="Multiple" /> is non-null then this
        /// member is set to the default value for its type.
        /// </remarks>
        public readonly T Single;

        /// <summary>
        /// An enumerator that can produce a sequence of
        /// values asynchronously, if this instance represents
        /// such a sequence of values.
        /// </summary>
        public readonly IAsyncEnumerable<T>? Multiple;

        /// <summary>
        /// Wrap a single value.
        /// </summary>
        public AsyncExpandable(T single)
        {
            Single = single;
            Multiple = null;
        }

        /// <summary>
        /// Wrap a sequence of values.
        /// </summary>
        public AsyncExpandable(IAsyncEnumerable<T> multiple)
        {
            Single = default!;
            Multiple = multiple;
        }
    }
}
