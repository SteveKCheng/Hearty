using JobBank.Scheduling;
using JobBank.Work;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server.WebApi
{
    /// <summary>
    /// Defines endpoints in an ASP.NET Core application to accept
    /// connections from remote workers in the Job Bank framework.
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
        /// at a WebSocket endpoint "/ws/worker".
        /// </summary>
        /// <param name="endpoints">
        /// Builds all the HTTP endpoints used by the application. 
        /// </param>
        /// <returns>
        /// Object that allows customizing the new endpoint further.
        /// </returns>
        public static IEndpointConventionBuilder
            MapRemoteWorkersWebSocket(this IEndpointRouteBuilder endpoints)
        {
            return MapRemoteWorkersWebSocket(endpoints, "/ws/worker");
        }

        /// <summary>
        /// Accept connections from remote worker hosts 
        /// at a WebSocket endpoint.
        /// </summary>
        /// <param name="endpoints">
        /// Builds all the HTTP endpoints used by the application. 
        /// </param>
        /// <param name="pattern">Route pattern for the WebSocket endpoint.
        /// Typically "/ws/worker".
        /// </param>
        /// <returns>
        /// Object that allows customizing the new endpoint further.
        /// </returns>
        public static IEndpointConventionBuilder
            MapRemoteWorkersWebSocket(this IEndpointRouteBuilder endpoints,
                                      string pattern)
        {
            return endpoints.Map(
                    pattern,
                    RemoteWorkersWebSocketEndpoint.Create(endpoints.ServiceProvider));
        }
    }

    internal class RemoteWorkersWebSocketEndpoint
    {
        private readonly ILogger _logger;
        private readonly WorkerDistribution<PromiseJob, PromiseData> _workerDistribution;

        private RemoteWorkersWebSocketEndpoint(
            ILogger<RemoteWorkersWebSocketEndpoint> logger,
            WorkerDistribution<PromiseJob, PromiseData> workerDistribution)
        {
            _logger = logger;
            _workerDistribution = workerDistribution;
        }

        private Task AcceptAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }

            return InvokeAsyncImpl(context);
        }

        private async Task InvokeAsyncImpl(HttpContext context)
        {
            _logger.LogInformation("Incoming WebSocket connection");

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync(
                new WebSocketAcceptContext
                {
                    DangerousEnableCompression = true,
                    DisableServerContextTakeover = true,
                    SubProtocol = WorkerHost.WebSocketsSubProtocol
                });

            var (worker, closeTask) =
                await RemoteWorkerService.AcceptHostAsync(_workerDistribution,
                                                          webSocket,
                                                          CancellationToken.None);
            await closeTask;
        }

        public static RequestDelegate Create(IServiceProvider services)
        {
            var self = new RemoteWorkersWebSocketEndpoint(
                    services.GetRequiredService<ILogger<RemoteWorkersWebSocketEndpoint>>(),
                    services.GetRequiredService<WorkerDistribution<PromiseJob, PromiseData>>()
                );
            return self.AcceptAsync;
        }
    }
}
