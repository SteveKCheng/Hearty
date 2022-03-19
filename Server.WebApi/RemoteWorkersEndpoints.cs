using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Hearty.Scheduling;
using Hearty.Utilities;
using Hearty.Work;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Linq;

namespace Hearty.Server.WebApi
{
    /// <summary>
    /// Defines endpoints in an ASP.NET Core application to accept
    /// connections from remote workers in the Hearty framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Currently, the sole protocol that is supported is WebSockets.
    /// </para>
    /// <para>
    /// The following component must be provided via ASP.NET Core's
    /// dependency injection: <see cref="WorkerDistribution{PromiseJob, PromiseData}" />.
    /// </para>
    /// </remarks>
    public static class RemoteWorkersEndpoints
    {
        /// <summary>
        /// Accept connections from remote worker hosts 
        /// at a WebSocket endpoint.
        /// </summary>
        /// <param name="endpoints">
        /// Builds all the HTTP endpoints used by the application. 
        /// </param>
        /// <param name="pattern">Route pattern for the WebSocket endpoint.
        /// Typically it is <see cref="WorkerHost.WebSocketsDefaultPath"/>, 
        /// which is the default is this parameter is null.
        /// </param>
        /// <param name="options">
        /// Options when accepting connections on the WebSocket endpoint.
        /// If null, a default is used.
        /// </param>
        /// <returns>
        /// Object that allows customizing the new endpoint further.
        /// </returns>
        public static IEndpointConventionBuilder
            MapRemoteWorkersWebSocket(this IEndpointRouteBuilder endpoints,
                                      string? pattern = null,
                                      RemoteWorkersWebSocketOptions? options = null)
        {
            return endpoints.Map(
                    pattern ?? WorkerHost.WebSocketsDefaultPath,
                    RemoteWorkersWebSocketEndpoint.CreateRequestDelegate(
                        endpoints.ServiceProvider, options));
        }

        /// <summary>
        /// Get the default URL to connect to the running HTTP server.
        /// </summary>
        /// <param name="server">
        /// Interface provided by ASP.NET Core that lets the server.
        /// addresses be queried.  It should be found by dependency
        /// injection.
        /// </param>
        /// <param name="path">
        /// Suffix to the path for the hosting server.
        /// This should typically be the same as "PathBase" set in the
        /// application's middleware.  It should be URL-escaped already.
        /// </param>
        /// <returns>
        /// Best guess as to the URL of the running HTTP server.
        /// It will be "https" if supported by the server.
        /// </returns>
        public static string GetDefaultHostUrl(this IServer server, string? path = null)
        {
            string siteUrl = server.Features
                                   .Get<IServerAddressesFeature>()
                                   ?.Addresses
                                   .FirstOrDefault()
                                ?? "http://localhost/";

            if (string.IsNullOrEmpty(path))
                return siteUrl;

            string separator = (siteUrl.EndsWith('/') || path.StartsWith('/'))
                                ? string.Empty : "/";
            string finalUrl = string.Concat(siteUrl, separator, path);
            return finalUrl;
        }
    }

    internal class RemoteWorkersWebSocketEndpoint
    {
        private readonly ILogger _logger;
        private readonly WorkerDistribution<PromisedWork, PromiseData> _workerDistribution;
        private readonly RemoteWorkersWebSocketOptions _options;

        private RemoteWorkersWebSocketEndpoint(
            ILogger<RemoteWorkersWebSocketEndpoint> logger,
            WorkerDistribution<PromisedWork, PromiseData> workerDistribution,
            RemoteWorkersWebSocketOptions options)
        {
            _logger = logger;
            _workerDistribution = workerDistribution;
            _options = options;
        }

        private Task AcceptAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }

            return AcceptAsyncImpl(context);
        }

        private async Task AcceptAsyncImpl(HttpContext context)
        {
            _logger.LogInformation("Incoming WebSocket connection");

            using var webSocket = await context.WebSockets
                                               .AcceptWebSocketAsync(_options);

            CancellationSourcePool.Use cancellationSourceUse = default;
            var timeout = _options.RegistrationTimeout;
            if (timeout != TimeSpan.Zero)
                cancellationSourceUse = CancellationSourcePool.CancelAfter(timeout);

            using var _ = cancellationSourceUse;

            var (worker, closeTask) =
                await RemoteWorkerService.AcceptHostAsync(_workerDistribution,
                                                          webSocket,
                                                          cancellationSourceUse.Token);
            await closeTask;
        }

        public static RequestDelegate 
            CreateRequestDelegate(IServiceProvider services,
                                  RemoteWorkersWebSocketOptions? options)
        {
            var self = new RemoteWorkersWebSocketEndpoint(
                    services.GetRequiredService<ILogger<RemoteWorkersWebSocketEndpoint>>(),
                    services.GetRequiredService<WorkerDistribution<PromisedWork, PromiseData>>(),
                    options ?? new RemoteWorkersWebSocketOptions()
                );
            return self.AcceptAsync;
        }
    }
}
