using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using JobBank.Scheduling;
using JobBank.Work;

namespace JobBank.Server.WebApi
{
    /// <summary>
    /// ASP.NET Core middleware that accepts 
    /// connections from remote workers in the JobBank framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Currently, the sole protocol that is supported is WebSockets.
    /// </para>
    /// <para>
    /// The WebSockets middleware
    /// <see cref="Microsoft.AspNetCore.WebSockets.WebSocketMiddleware" />
    /// must be installed in the ASP.NET Core pipeline before
    /// this middleware is installed.
    /// </para>
    /// </remarks>
    public class RemoteWorkersMiddleware
    {
        private readonly ILogger _logger;
        private readonly WorkerDistribution<PromiseJob, PromiseData> _workerDistribution;
        private readonly RequestDelegate _next;

        /// <summary>
        /// Construct this middleware with its required services.
        /// </summary>
        /// <param name="logger">Logs connections from remote hosts. </param>
        /// <param name="workerDistribution">
        /// Remote workers that successfully connect are registered here.
        /// </param>
        /// <param name="next">
        /// The next middleware in ASP.NET Core's pipeline.
        /// </param>
        public RemoteWorkersMiddleware(ILogger<RemoteWorkersMiddleware> logger,
                                       WorkerDistribution<PromiseJob, PromiseData> workerDistribution,
                                       RequestDelegate next)
        {
            _logger = logger;
            _workerDistribution = workerDistribution;
            _next = next;
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

        /// <summary>
        /// Invokes this middleware as part of ASP.NET Core's pipeline.
        /// </summary>
        public Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Path != "/ws/worker")
                return _next(context);

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }

            return InvokeAsyncImpl(context);
        }
    }
}
