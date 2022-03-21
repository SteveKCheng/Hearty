using Hearty.Work;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Server.WebUi
{
    /// <summary>
    /// Specifies custom settings for dashboard displays on an instance
    /// of a Hearty job server.
    /// </summary>
    /// <remarks>
    /// An instance of this class is injected as a dependency into
    /// the Web UI pages.  This class is meant to only contain
    /// settings that are customizable by the developer but not
    /// the user.
    /// </remarks>
    public sealed class DisplaySpecialization
    {
        /// <summary>
        /// List of custom properties to display when job details are expanded.
        /// </summary>
        public IReadOnlyList<string> JobCustomProperties { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The HTTP or HTTPS URL for the job server, including
        /// any "PathBase" suffix.
        /// </summary>
        public string ServerUrl { get; init; } = "http://localhost/";

        /// <summary>
        /// The URL for workers to connect to the job server 
        /// via WebSockets.
        /// </summary>
        public Uri? WorkersWebSocketsUrl { get; init; }

        /// <summary>
        /// Generates the implementation object for a mock worker host.
        /// </summary>
        /// <remarks>
        /// Setting this delegate (to non-null) causes a button to
        /// appear under the "workers" page to create new hosts.
        /// This feature is meant for testing.
        /// </remarks>
        public Func<RegisterWorkerRequestMessage, IJobSubmission>? WorkerFactory { get; init; }
    }
}
