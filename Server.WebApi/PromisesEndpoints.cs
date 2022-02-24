using JobBank.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System;
using System.IO;
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
            public IRemoteJobCancellation RemoteCancellation { get; init; }

            /// <summary>
            /// Obtains the instance of <see cref="IJobQueueOwner"/> 
            /// given the client's credentials and any explicitly specified
            /// queue owner in the incoming request.
            /// </summary>
            public JobQueueOwnerRetriever? JobQueueOwnerRetrieval { get; init; }
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
                RemoteCancellation = p.GetService<IRemoteJobCancellation>()
                                        ?? _dummyRemoteCancellation,
                JobQueueOwnerRetrieval = p.GetService<JobQueueOwnerRetriever>()
            };
        }

        /// <summary>
        /// Dummy implementation for <see cref="Services.RemoteCancellation" />
        /// if there is none supplied by dependency injection.
        /// </summary>
        private static readonly IRemoteJobCancellation _dummyRemoteCancellation
            = new DummyRemoteCancellation();

        /// <summary>
        /// Dummy implementation for <see cref="Services.RemoteCancellation" />
        /// if there is none supplied by dependency injection.
        /// </summary>
        private class DummyRemoteCancellation : IRemoteJobCancellation
        {
            public bool TryCancelJobForClient(JobQueueKey queueKey, PromiseId target)
                => false;

            public bool TryKillJob(PromiseId target) => false;
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

                httpResponse.ContentType = output.ContentType;
                httpResponse.ContentLength = output.ContentLength;

                var writer = httpResponse.BodyWriter;
                await output.WriteToPipeAsync(format: 0,
                                              writer, position: 0,
                                              cancellationToken: CancellationToken.None);
                await writer.CompleteAsync();
            }
        }

        private static string? ParseQueueName(StringValues input)
        {
            if (input.Count == 0)
                return null;

            if (input.Count > 1)
                throw new InvalidDataException("There is more than one queue name present. ");

            var name = input[0].Trim();
            if (name.Length == 0 || HasWhitespace(name))
                throw new InvalidDataException("The queue name is invalid. ");

            return name;
        }

        private static bool HasWhitespace(ReadOnlySpan<char> input)
        {
            for (int i = 0; i < input.Length; ++i)
            {
                if (char.IsWhiteSpace(input[i]))
                    return true;
            }

            return false;
        }

        private static int? ParseQueuePriority(StringValues input)
        {
            if (input.Count == 0)
                return null;

            if (input.Count > 1)
                throw new InvalidDataException("There is more than one priority specified. ");

            if (!int.TryParse(input[0], out int priority) || priority < 0)
                throw new InvalidDataException("Job priority is incorrect. ");

            return priority;
        }

        private static async ValueTask<JobQueueKey?> 
            GetJobQueueKey(Services services, HttpContext httpContext)
        {
            var httpRequest = httpContext.Request;

            var queueName = ParseQueueName(httpRequest.Query["queue"])
                            ?? ParseQueueName(httpRequest.Headers[JobBankHttpHeaders.JobQueueName])
                            ?? "default";
            var priority = ParseQueuePriority(httpRequest.Query["priority"])
                            ?? ParseQueuePriority(httpRequest.Headers[JobBankHttpHeaders.JobPriority])
                            ?? 5;

            IJobQueueOwner? queueOwner = null;
            if (services.JobQueueOwnerRetrieval is not null)
            {
                queueOwner = await services.JobQueueOwnerRetrieval
                                           .Invoke(httpContext.User, null)
                                           .ConfigureAwait(false);
            }

            return queueOwner is not null
                    ? new JobQueueKey(queueOwner, priority, queueName)
                    : null;
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
                JobQueueKey? queueKey = await GetJobQueueKey(services, httpContext)
                                                .ConfigureAwait(false);

                var jobInput = new PromiseRequest
                {
                    Storage = services.PromiseStorage,
                    Directory = services.PathsDirectory,
                    RouteKey = routeKey,
                    JobQueueKey = queueKey,
                    ContentType = httpRequest.ContentType,
                    ContentLength = httpRequest.ContentLength,
                    PipeReader = httpRequest.BodyReader,
                    CancellationToken = cancellationToken
                };

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
            httpResponse.Headers.Add(JobBankHttpHeaders.PromiseId, promiseId.ToString());

            httpResponse.Headers.Location = 
                httpRequest.PathBase.Add($"/jobs/v1/id/{promiseId}").ToString();
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
        /// <returns>Builder specific to the new endpoint that may be
        /// used to customize its handling by the ASP.NET Core framework.
        /// </returns>
        public static IEndpointConventionBuilder
            MapCancelJobById(this IEndpointRouteBuilder endpoints)
        {
            var services = endpoints.GetServices();
            return endpoints.MapDelete(
                    "/jobs/v1/id/{serviceId}/{sequenceNumber}",
                    httpContext => CancelJobAsync(services, httpContext, kill: false));
        }

        /// <summary>
        /// Maps the HTTP route that enables an administrator to kill a job
        /// (for all clients).
        /// </summary>
        /// <param name="endpoints">Builds all the HTTP endpoints used by the application. </param>
        /// <returns>Builder specific to the new endpoint that may be
        /// used to customize its handling by the ASP.NET Core framework.
        /// Typically it should be set to require administrator-level authorization.
        /// </returns>
        public static IEndpointConventionBuilder
            MapKillJobById(this IEndpointRouteBuilder endpoints)
        {
            var services = endpoints.GetServices();
            return endpoints.MapDelete(
                    "/jobs/v1/admin/id/{serviceId}/{sequenceNumber}",
                    httpContext => CancelJobAsync(services, httpContext, kill: true));
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

            var parameters = new QueryParameters
            {
                PromiseId = promiseId,
                Timeout = timeout,
                AcceptedContentTypes = httpRequest.Headers.Accept
            };

            return RespondToGetPromiseAsync(services, httpResponse, 
                                            parameters, 
                                            client, cancellationToken);
        }

        private static bool TryParsePromiseId(HttpContext httpContext, out PromiseId promiseId)
        {
            var serviceIdStr = (string)httpContext.GetRouteValue("serviceId")!;
            var sequenceNumberStr = (string)httpContext.GetRouteValue("sequenceNumber")!;
            return PromiseId.TryParse(serviceIdStr, sequenceNumberStr, out promiseId);
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

        /// <summary>
        /// Input parameters from the client's query 
        /// to <see cref="RespondToGetPromiseAsync" />.
        /// </summary>
        private readonly struct QueryParameters
        {
            public PromiseId PromiseId { get; init; }

            public TimeSpan Timeout { get; init; }

            public StringValues AcceptedContentTypes { get; init; }
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

            var parameters = new QueryParameters
            {
                PromiseId = promiseId,
                Timeout = timeout,
                AcceptedContentTypes = httpRequest.Headers.Accept
            };

            return RespondToGetPromiseAsync(services, httpResponse,
                                            parameters,
                                            client, cancellationToken);
        }

        private static async Task CancelJobAsync(Services services,
                                                 HttpContext httpContext,
                                                 bool kill)
        {
            var httpResponse = httpContext.Response;

            var serviceIdStr = (string)httpContext.GetRouteValue("serviceId")!;
            var sequenceNumberStr = (string)httpContext.GetRouteValue("sequenceNumber")!;
            if (!PromiseId.TryParse(serviceIdStr, sequenceNumberStr, out var promiseId))
            {
                httpResponse.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            bool success;

            if (kill)
            {
                success = services.RemoteCancellation.TryKillJob(promiseId);
            }
            else
            {
                var queueKey = (await GetJobQueueKey(services, httpContext).ConfigureAwait(false))
                                    ?? throw new InvalidOperationException("Queue must be specified. ");

                success = services.RemoteCancellation
                                  .TryCancelJobForClient(queueKey, promiseId);
            }

            httpResponse.StatusCode = success ? StatusCodes.Status202Accepted
                                              : StatusCodes.Status404NotFound;
        }

        private static async Task RespondToGetPromiseAsync(Services services,
                                                           HttpResponse httpResponse,
                                                           QueryParameters parameters,
                                                           IPromiseClientInfo client,
                                                           CancellationToken cancellationToken)
        {
            var promise = services.PromiseStorage.GetPromiseById(parameters.PromiseId);
            if (promise == null)
            {
                httpResponse.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (parameters.Timeout == TimeSpan.Zero && !promise.IsCompleted)
            {
                httpResponse.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            using var result = await promise.GetResultAsync(client, 
                                                            parameters.Timeout, 
                                                            cancellationToken)
                                            .ConfigureAwait(false);
            var output = result.NormalOutput;

            int format = output.NegotiateFormat(parameters.AcceptedContentTypes);
            if (format < 0)
            {
                httpResponse.StatusCode = StatusCodes.Status406NotAcceptable;
                return;
            }

            httpResponse.StatusCode = StatusCodes.Status200OK;
            httpResponse.ContentType = output.GetFormatInfo(format).MediaType;
            httpResponse.ContentLength = output.GetContentLength(format);

            await output.WriteToPipeAsync(format,
                                          httpResponse.BodyWriter,
                                          0,
                                          cancellationToken)
                        .ConfigureAwait(false);
            await httpResponse.BodyWriter.CompleteAsync();
        }
    }
}
