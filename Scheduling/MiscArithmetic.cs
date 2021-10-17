using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Miscellaneous low-level operations on numbers.
    /// </summary>
    internal static class MiscArithmetic
    {
        /// <summary>
        /// Add two signed 32-bit integers, capping or flooring the result
        /// if it overflows the capacity of a 32-bit integer.
        /// </summary>
        /// <param name="x">One of the integers to add together. </param>
        /// <param name="y">The other integer to add together. </param>
        /// <returns>The saturated sum. </returns>
        public static int SaturatingAdd(int x, int y)
        {
            uint ux = (uint)x;
            uint uy = (uint)y;
            uint uz = unchecked(ux + uy);

            // Calculate the result if there is overflow:
            //   x >= 0 ? int.MaxValue : int.MinValue
            uint uw = (ux >> 31) + (uint)int.MaxValue;

            // Overflow occur if and only if
            //   a. x and y have the same signs
            //      (ux ^ uy has zero as its top bit), AND
            //   b. x and z have different signs
            //      (ux ^ uz has one as its top bit).
            //
            // The boolean test below has the negation of the
            // above conditions.
            return (int)((ux ^ uy) | ~(ux ^ uz)) < 0 ? (int)uz 
                                                     : (int)uw;
        }

        /// <summary>
        /// Subtract two signed 32-bit integers, capping or flooring the result
        /// if it overflows the capacity of a 32-bit integer.
        /// </summary>
        /// <param name="x">The integer to subtract from (minuend). </param>
        /// <param name="y">The integer to subtract (subtrahend). </param>
        /// <returns>The saturated difference. </returns>
        public static int SaturatingSubtract(int x, int y)
        {
            uint ux = (uint)x;
            uint uy = (uint)y;
            uint uz = unchecked(ux - uy);

            // Calculate the result if there is overflow:
            //   x >= 0 ? int.MaxValue : int.MinValue
            uint uw = (ux >> 31) + (uint)int.MaxValue;

            // Overflow occur if and only if
            //   a. x and y have different signs
            //      (ux ^ uy has one as its top bit), AND
            //   b. x and z have different signs
            //      (ux ^ uz has one as its top bit).
            //
            // The boolean test below has the negation of the
            // above conditions, plus one further application
            // of DeMorgan's law: ~(~a | ~b) = a & b.
            return (int)((ux ^ uy) & (ux ^ uz)) >= 0 ? (int)uz
                                                     : (int)uw;
        }
    }
}
