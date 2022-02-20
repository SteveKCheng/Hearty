using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Common
{
    /// <summary>
    /// Custom HTTP header keys used by the Job Bank ReST API.
    /// </summary>
    /// <remarks>
    /// These header keys are in mixed case for readability.
    /// ASP.NET Core automatically takes the lowercase when
    /// putting them in HTTP/2+ headers.
    /// </remarks>
    public static class JobBankHttpHeaders
    {
        /// <summary>
        /// Header that reports the promise ID in the response.
        /// </summary>
        public static readonly string PromiseId = "X-Promise-Id";

        /// <summary>
        /// Header within an item in a multi-part body
        /// that reports the index of the item in its original
        /// ordering.
        /// </summary>
        public static readonly string Ordinal = "Ordinal";
    }
}
