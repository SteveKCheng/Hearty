using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Common
{
    /// <summary>
    /// Custom HTTP header keys used by the Hearty ReST API.
    /// </summary>
    /// <remarks>
    /// These header keys are in mixed case for readability.
    /// ASP.NET Core automatically takes the lowercase when
    /// putting them in HTTP/2+ headers.
    /// </remarks>
    public static class HeartyHttpHeaders
    {
        /// <summary>
        /// Header that reports the promise ID in the response.
        /// </summary>
        public static readonly string PromiseId = "X-Promise-Id";

        /// <summary>
        /// Content type desired by the client for the items 
        /// in a data stream that is a container.
        /// </summary>
        public static readonly string AcceptItem = "Accept-Item";

        /// <summary>
        /// Header within an item in a multi-part body
        /// that reports the index of the item in its original
        /// ordering.
        /// </summary>
        public static readonly string Ordinal = "Ordinal";

        /// <summary>
        /// Header for the client to specify the name of the desired job queue.
        /// </summary>
        public static readonly string JobCohort = "Job-Cohort";

        /// <summary>
        /// Header for the client to specify the desired priority of a job.
        /// </summary>
        public static readonly string JobPriority = "Job-Priority";
    }
}
