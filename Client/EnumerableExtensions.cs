using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Client
{
    internal static class EnumerableExtensions
    {
        /// <summary>
        /// Same as <see cref="Enumerable.SingleOrDefault{TSource}(IEnumerable{TSource})" />
        /// but does not throw an exception if there is more than one element in the sequence.
        /// </summary>
        /// <typeparam name="TSource">The type of the item in the sequence. </typeparam>
        /// <param name="source">The sequence to examine. </param>
        /// <returns>
        /// The one element in the sequence, or null if there is none
        /// or more than one element.
        /// </returns>
        public static TSource? SingleOrDefaultNoException<TSource>(this IEnumerable<TSource> source)
        {
            bool hasResult = false;
            TSource? result = default;
            foreach (var item in source)
            {
                if (hasResult)
                    return default;

                result = item;
                hasResult = true;
            }

            return result;
        }
    }
}
