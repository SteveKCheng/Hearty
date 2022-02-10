using JobBank.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server.WebApi
{
    /// <summary>
    /// Exposes promises from a Job Bank server to HTTP clients. 
    /// </summary>
    /// <remarks>
    /// As the HTTP endpoints need to expose arbitrary payloads efficiently,
    /// the MVC (Model-View-Controller) framework and model-binding are not used.
    /// HTTP endpoints are implemented in the "raw" ASP.NET Core API.
    /// </remarks>
    public static class PromisesEndpoints
    {
        /// <summary>
        /// Accept requests for promises 
        /// of a certain type to be posted at a specific HTTP endpoint. 
        /// </summary>
        /// <param name="endpoints">Builds all the HTTP endpoints used by the application. </param>
        /// <param name="routeKey">Sub-path occurring after the URL prefix for Job Bank that 
        /// is specific to job executor being registered.
        /// </param>
        /// <param name="executor">Processes the requests for promises
        /// and returns them by ID.  The ID can be either an existing promise 
        /// or a newly created promise that will provide its result 
        /// (asynchronously).
        /// </param>
        /// <returns>Builder specific to the new job executor's endpoint that may be
        /// used to customize its handling by the ASP.NET Core framework.
        /// </returns>
        public static IEndpointConventionBuilder 
            MapPostJob(this IEndpointRouteBuilder endpoints,
                       string routeKey,
                       Func<PromiseRequest, ValueTask<PromiseId>> executor)
        {
            var services = endpoints.GetServices();
            routeKey = routeKey.Trim('/');
            return endpoints.MapPost(
                    "/jobs/v1/queue/" + routeKey,
                    httpContext => PostJobAsync(services, httpContext, routeKey, executor));
        }

        /// <summary>
        /// Groups the dependencies and settings needed to implement
        /// Web APIs for promises.
        /// </summary>
        private readonly struct Services
        {
            /// <summary>
            /// Looks up and creates promises, needed for all
            /// endpoints.
            /// </summary>
            public PromiseStorage PromiseStorage { get; init; }

            /// <summary>
            /// Services endpoints for promises by path,
            /// and for creating promises initially.
            /// </summary>
            public PathsDirectory PathsDirectory { get; init; }

            /// <summary>
            /// Called to translate exceptions that come from
            /// user-defined functions involving promises.
            /// </summary>
            public PromiseExceptionTranslator ExceptionTranslator { get; init; }

            /// <summary>
            /// Services requests for cancelling previously
            /// created promises.
            /// </summary>
            public IRemoteCancellation<PromiseId> RemoteCancellation { get; init; }
        }

        /// <summary>
        /// Retrieve the <see cref="PromiseStorage" /> instance
        /// that has been dependency-injected in the ASP.NET Core application.
        /// </summary>
        private static Services
            GetServices(this IEndpointRouteBuilder endpoints)
        {
            var p = endpoints.ServiceProvider;
            return new Services
            {
                PromiseStorage = p.GetRequiredService<PromiseStorage>(),
                PathsDirectory = p.GetRequiredService<PathsDirectory>(),
                ExceptionTranslator = p.GetRequiredService<PromiseExceptionTranslator>(),
                RemoteCancellation = p.GetService<IRemoteCancellation<PromiseId>>()
                                        ?? _dummyRemoteCancellation
            };
        }

        /// <summary>
        /// Dummy implementation for <see cref="Services.RemoteCancellation" />
        /// if there is none supplied by dependency injection.
        /// </summary>
        private static readonly IRemoteCancellation<PromiseId> _dummyRemoteCancellation
            = new DummyRemoteCancellation();

        /// <summary>
        /// Dummy implementation for <see cref="Services.RemoteCancellation" />
        /// if there is none supplied by dependency injection.
        /// </summary>
        private class DummyRemoteCancellation : IRemoteCancellation<PromiseId>
        {
            public ValueTask<CancellationStatus> TryCancelAsync(ClaimsPrincipal client, PromiseId target, bool force)
                => ValueTask.FromResult(CancellationStatus.NotSupported);
        }

        /// <summary>
        /// Translate a .NET exception to an HTTP response to the client as best
        /// as possible.
        /// </summary>
        private static async Task TranslateExceptionToHttpResponseAsync(PromiseExceptionTranslator translator,
                                                                        Exception exception,
                                                                        HttpResponse httpResponse)
        {
            if (exception is PayloadTooLargeException)
            {
                httpResponse.StatusCode = StatusCodes.Status413PayloadTooLarge;
            }
            else
            {
                httpResponse.StatusCode = StatusCodes.Status400BadRequest;

                var output = translator.Invoke(null, exception);

                httpResponse.ContentType = output.SuggestedContentType;
                httpResponse.ContentLength = output.ContentLength;

                var writer = httpResponse.BodyWriter;
                await output.WriteToPipeAsync(output.SuggestedContentType,
                                              writer, position: 0,
                                              cancellationToken: CancellationToken.None);
                await writer.CompleteAsync();
            }
        }

        /// <summary>
        /// Invokes a job executor to process a job posted by an HTTP client.
        /// </summary>
        private static async Task PostJobAsync(Services services, 
                                               HttpContext httpContext,
                                               string routeKey,
                                               Func<PromiseRequest, ValueTask<PromiseId>> executor)
        {
            var httpRequest = httpContext.Request;
            var httpResponse = httpContext.Response;
            var cancellationToken = httpContext.RequestAborted;
            PromiseId promiseId;

            try
            {
                var jobInput = new PromiseRequest(services.PromiseStorage,
                                                  services.PathsDirectory,
                                                  httpRequest.ContentType,
                                                  httpRequest.ContentLength,
                                                  httpRequest.BodyReader,
                                                  cancellationToken);

                promiseId = await executor.Invoke(jobInput)
                                          .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // FIXME remove the Promise object here
                await TranslateExceptionToHttpResponseAsync(services.ExceptionTranslator, 
                                                            e, 
                                                            httpResponse)
                    .ConfigureAwait(false);
                return;
            }

            httpResponse.StatusCode = StatusCodes.Status303SeeOther;
            httpResponse.Headers.Add("x-promise-id", promiseId.ToString());

            httpResponse.Headers.Add("location", 
                httpRequest.PathBase.Add($"/jobs/v1/id/{promiseId}").ToString());
        }

        /// <summary>
        /// Maps the HTTP route that reads out to the client a cached promise given its ID.
        /// </summary>
        /// <param name="endpoints">Builds all the HTTP endpoints used by the application. </param>
        /// <returns>Builder specific to the new job executor's endpoint that may be
        /// used to customize its handling by the ASP.NET Core framework.
        /// </returns>
        public static IEndpointConventionBuilder
            MapGetPromiseById(this IEndpointRouteBuilder endpoints)
        {
            var services = endpoints.GetServices();

            // FIXME This should be managed by a cache
            IPromiseClientInfo clientInfo = new BasicPromiseClientInfo();

            return endpoints.MapGet(
                    pattern: "/jobs/v1/id/{serviceId}/{sequenceNumber}",
                    requestDelegate: httpContext => GetPromiseByIdAsync(services, 
                                                                        httpContext, 
                                                                        clientInfo));
        }

        /// <summary>
        /// Maps the HTTP route that enables a client to cancel a created promise.
        /// </summary>
        /// <param name="endpoints">Builds all the HTTP endpoints used by the application. </param>
        /// <returns>Builder specific to the new job executor's endpoint that may be
        /// used to customize its handling by the ASP.NET Core framework.
        /// </returns>
        public static IEndpointConventionBuilder
            MapPostPromiseById(this IEndpointRouteBuilder endpoints)
        {
            var services = endpoints.GetServices();

            // FIXME This should be managed by a cache
            IPromiseClientInfo clientInfo = new BasicPromiseClientInfo();

            return endpoints.MapPost(
                    pattern: "/jobs/v1/id/{serviceId}/{sequenceNumber}",
                    requestDelegate: httpContext => PostPromiseByIdAsync(services,
                                                                         httpContext));
        }

        /// <summary>
        /// Maps the HTTP route that reads out to the client a cached promise given one
        /// if its (named) paths.
        /// </summary>
        /// <param name="endpoints">Builds all the HTTP endpoints used by the application. </param>
        /// <returns>Builder specific to the new job executor's endpoint that may be
        /// used to customize its handling by the ASP.NET Core framework.
        /// </returns>
        public static IEndpointConventionBuilder
            MapGetPromiseByPath(this IEndpointRouteBuilder endpoints)
        {
            var services = endpoints.GetServices();

            // FIXME This should be managed by a cache
            IPromiseClientInfo clientInfo = new BasicPromiseClientInfo();

            return endpoints.MapGet(
                    pattern: "/jobs/v1/index/{**path}",
                    requestDelegate: httpContext => GetPromiseByPathAsync(services,
                                                                          httpContext,
                                                                          clientInfo));
        }

        private static Task GetPromiseByIdAsync(Services services, 
                                                HttpContext httpContext,
                                                IPromiseClientInfo client)
        {
            var httpRequest = httpContext.Request;
            var httpResponse = httpContext.Response;
            var cancellationToken = httpContext.RequestAborted;

            if (!TryParsePromiseId(httpContext, out var promiseId))
            {
                httpResponse.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }

            var timeout = ParseTimeout(httpRequest.Query);

            return RespondToGetPromiseAsync(services, httpResponse, 
                                            promiseId, timeout, 
                                            client, cancellationToken);
        }

        private static bool TryParsePromiseId(HttpContext httpContext, out PromiseId promiseId)
        {
            var serviceIdStr = (string)httpContext.GetRouteValue("serviceId")!;
            var sequenceNumberStr = (string)httpContext.GetRouteValue("sequenceNumber")!;
            return PromiseId.TryParse(serviceIdStr, sequenceNumberStr, out promiseId);
        }

        private static Task PostPromiseByIdAsync(Services services, 
                                                 HttpContext httpContext)
        {
            var httpRequest = httpContext.Request;
            var httpResponse = httpContext.Response;
            var cancellationToken = httpContext.RequestAborted;

            bool success = TryParsePromiseId(httpContext, out var promiseId) &
                           TryParseEnum<PromisePostAction>(httpContext, "action", out var action);

            if (success)
            {
                switch (action)
                {
                    case PromisePostAction.Cancel:
                        return RespondToCancelPromiseAsync(services, httpResponse, promiseId,
                                                           httpContext.User, cancellationToken);
                }
            }

            httpResponse.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        private enum PromisePostAction
        {
            Cancel
        }

        private static bool TryParseEnum<T>(HttpContext httpContext, string key, out T value)
            where T : struct, Enum
        {
            var httpRequest = httpContext.Request;
            if (!httpRequest.Query.TryGetValue(key, out var strings) ||
                strings.Count != 1)
            {
                value = default;
                return false;
            }

            return Enum.TryParse<T>(strings[0], ignoreCase: true, out value);
        }

        private static TimeSpan ParseTimeout(IQueryCollection queryParams)
        {
            if (!queryParams.TryGetValue("timeout", out var timeoutString))
                timeoutString = StringValues.Empty;
            var timeout = TimeSpan.Zero;
            if (timeoutString.Count == 1 && !string.IsNullOrEmpty(timeoutString[0]))
                RestApiUtilities.TryParseTimespan(timeoutString[0], out timeout);
            return timeout;
        }

        private static Task GetPromiseByPathAsync(Services services,
                                                  HttpContext httpContext,
                                                  IPromiseClientInfo client)
        {
            var httpRequest = httpContext.Request;
            var httpResponse = httpContext.Response;
            var cancellationToken = httpContext.RequestAborted;

            var pathString = (string)httpContext.GetRouteValue("path")!;
            var timeout = ParseTimeout(httpRequest.Query);

            var path = PromisePath.Parse(pathString);
            if (!services.PathsDirectory.TryGetPath(path, out var promiseId))
            {
                httpResponse.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return RespondToGetPromiseAsync(services, httpResponse,
                                            promiseId, timeout,
                                            client, cancellationToken);
        }

        private static Task CancelPromiseByIdAsync(Services services, 
                                                   HttpContext httpContext,
                                                   IPromiseClientInfo client)
        {
            var httpRequest = httpContext.Request;
            var httpResponse = httpContext.Response;
            var cancellationToken = httpContext.RequestAborted;

            var serviceIdStr = (string)httpContext.GetRouteValue("serviceId")!;
            var sequenceNumberStr = (string)httpContext.GetRouteValue("sequenceNumber")!;
            if (!PromiseId.TryParse(serviceIdStr, sequenceNumberStr, out var promiseId))
            {
                httpResponse.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }

            return RespondToCancelPromiseAsync(services,
                                               httpResponse,
                                               promiseId,
                                               httpContext.User,
                                               cancellationToken);
        }

        private static async Task RespondToCancelPromiseAsync(Services services, 
                                                              HttpResponse response,
                                                              PromiseId promiseId,
                                                              ClaimsPrincipal principal,
                                                              CancellationToken cancellationToken)
        {
            var status = await services.RemoteCancellation
                                       .TryCancelAsync(principal, promiseId, force: false)
                                       .ConfigureAwait(false);

            response.StatusCode = status switch
            {
                CancellationStatus.Cancelled => StatusCodes.Status202Accepted,
                CancellationStatus.NotFound => StatusCodes.Status404NotFound,
                CancellationStatus.Forbidden => StatusCodes.Status403Forbidden,
                CancellationStatus.NotSupported => StatusCodes.Status501NotImplemented,
                _ => StatusCodes.Status500InternalServerError
            };
        }

        private static async Task RespondToGetPromiseAsync(Services services,
                                                           HttpResponse httpResponse,
                                                           PromiseId promiseId,
                                                           TimeSpan timeout,
                                                           IPromiseClientInfo client,
                                                           CancellationToken cancellationToken)
        {
            var promise = services.PromiseStorage.GetPromiseById(promiseId);
            if (promise == null)
            {
                httpResponse.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (timeout == TimeSpan.Zero && !promise.IsCompleted)
            {
                httpResponse.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            using var result = await promise.GetResultAsync(client, timeout, cancellationToken)
                                            .ConfigureAwait(false);
            var output = result.NormalOutput;

            httpResponse.StatusCode = StatusCodes.Status200OK;
            httpResponse.ContentType = output.SuggestedContentType;
            httpResponse.ContentLength = output.ContentLength;

            await output.WriteToPipeAsync(output.SuggestedContentType,
                                          httpResponse.BodyWriter,
                                          0,
                                          cancellationToken)
                        .ConfigureAwait(false);
            await httpResponse.BodyWriter.CompleteAsync();
        }
    }
}
