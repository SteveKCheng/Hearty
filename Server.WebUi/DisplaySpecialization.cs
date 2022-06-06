using Hearty.Work;
using Microsoft.AspNetCore.Components;
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
        /// <remarks>
        /// This URL may be displayed on status pages.  
        /// If this property is null (default),
        /// then the URL will be obtained from 
        /// <see cref="NavigationManager.BaseUri" />;
        /// this may not be correct if the server is running behind
        /// an ingress or proxy.
        /// </remarks>
        public string? ServerUrl { get; init; }

        /// <summary>
        /// The URL for workers to connect to the job server 
        /// via WebSockets.
        /// </summary>
        public Uri? WorkersWebSocketsUrl { get; init; }

        /// <summary>
        /// Get <see cref="ServerUrl" /> with defaulting if it has not been set.
        /// </summary>
        internal string GetServerUrl(NavigationManager navigationManager)
            => ServerUrl ?? navigationManager.BaseUri;

        /// <summary>
        /// Get <see cref="WorkersWebSocketsUrl" /> with defaulting if it has not been set.
        /// </summary>
        internal Uri GetWorkersWebSocketsUrl(NavigationManager navigationManager)
            => WorkersWebSocketsUrl 
            ?? WorkerHost.DeriveWebSocketUrl(GetServerUrl(navigationManager));
    }
}
