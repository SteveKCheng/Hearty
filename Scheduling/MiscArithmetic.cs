using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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

        /// <summary>
        /// Cap the magnitude of a 64-bit signed integer so that
        /// it can be stored as a 32-bit signed integer.
        /// </summary>
        /// <param name="number">
        /// The 64-bit signed integer as input.
        /// </param>
        /// <returns>
        /// <paramref name="number"/> casted as 32-bit if its
        /// value remains unchanged; otherwise the result is
        /// <see cref="int.MaxValue"/> or <see cref="int.MinValue"/>
        /// depending on the value being positive or negative,
        /// respectively.
        /// </returns>
        public static int SaturateToInt(long number)
        {
            unchecked
            {
                // High 32-bit word of number
                uint uh = (uint)((ulong)number >> 32);

                // Low 32-bit word of number
                uint ul = (uint)(ulong)number;

                // Calculate the result if there is overflow:
                //   number >= 0 ? int.MaxValue : int.MinValue
                uint uo = (uh >> 31) + (uint)int.MaxValue;

                // number is not overflowing the capacity of Int32
                // if and only if: 
                //   Ⓐ when it is >= 0, then uh is 0; or
                //   Ⓑ when it is < 0, then uh is -1.
                //
                // Unfortunately, while the GNU C/C++ compiler and
                // Clang do translate the following statement to
                // a conditional move instruction, RyuJit translates
                // to a conditional branch.
                return uh + (ul >> 31) == 0 ? (int)ul : (int)uo;
            }
        }

        /// <summary>
        /// Decrement an unsigned integer without underflowing.
        /// </summary>
        /// <param name="location">
        /// The variable for the 32-bit unsigned integer.
        /// </param>
        /// <returns>
        /// The value before decrementing.  Note this is different
        /// from <see cref="Interlocked.Decrement(ref uint)" />
        /// which returns the value after decrementing.
        /// </returns>
        public static uint InterlockedSaturatedDecrement(ref uint location)
        {
            uint s = location;
            uint r;
            do
            {
                r = s;
                if (r == 0)
                    return 0;

                s = Interlocked.CompareExchange(ref location, r - 1, r);
            } while (s != r);

            return r;
        }
    }
}
