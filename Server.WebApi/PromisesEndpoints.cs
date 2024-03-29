﻿using Hearty.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.WebApi
{
    /// <summary>
    /// Exposes promises from a Hearty server to HTTP clients. 
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
        /// <param name="routeKey">Sub-path occurring after the URL prefix for Hearty that 
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
            var services = new Services(endpoints.ServiceProvider);

            routeKey = routeKey.Trim('/');
            return endpoints.MapPost(
                    "/requests/" + routeKey,
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
            /// Maps the client's credentials and any explicitly specified
            /// queue owner in the incoming request to
            /// <see cref="JobQueueKey.Owner" />.
            /// </summary>
            public JobQueueOwnerRetriever? JobQueueOwnerRetrieval { get; init; }

            /// <summary>
            /// Collect the services used by methods of <see cref="PromisesEndpoints" />
            /// that have been dependency-injected in the ASP.NET Core application.
            /// </summary>
            public Services(IServiceProvider p)
            {
                PromiseStorage = p.GetRequiredService<PromiseStorage>();
                ExceptionTranslator = p.GetRequiredService<PromiseExceptionTranslator>();
                RemoteCancellation = p.GetService<IRemoteJobCancellation>()
                                        ?? _dummyRemoteCancellation;
                JobQueueOwnerRetrieval = p.GetService<JobQueueOwnerRetriever>();
            }
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
        private static async Task TranslateExceptionToHttpResponseAsync(object state,
                                                                        PromiseExceptionTranslator translator,
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

                var output = await translator.Invoke(state, null, exception)
                                             .ConfigureAwait(false);

                httpResponse.ContentType = output.ContentType;
                httpResponse.ContentLength = output.ContentLength;

                var writer = httpResponse.BodyWriter;
                await output.WriteToPipeAsync(writer: writer,
                                              format: 0,
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

        private static async ValueTask<JobQueueKey> 
            GetJobQueueKey(Services services, HttpContext httpContext)
        {
            var httpRequest = httpContext.Request;

            string? cohort = ParseQueueName(httpRequest.Query["cohort"])
                             ?? ParseQueueName(httpRequest.Headers[HeartyHttpHeaders.JobCohort]);
            int? priority = ParseQueuePriority(httpRequest.Query["priority"])
                            ?? ParseQueuePriority(httpRequest.Headers[HeartyHttpHeaders.JobPriority]);

            string? owner = null;
            if (services.JobQueueOwnerRetrieval is not null)
            {
                owner = await services.JobQueueOwnerRetrieval
                                      .Invoke(httpContext.User, null)
                                      .ConfigureAwait(false);
            }

            return new JobQueueKey(owner, priority, cohort);
        }

        private static bool? ParseBoolQueryParameter(IQueryCollection query, string key)
        {
            query.TryGetValue(key, out var values);

            for (int i = values.Count; i > 0; --i)
            {
                if (bool.TryParse(values[i - 1], out bool result))
                    return result;
            }

            return null;
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
            bool redirectLocally;

            try
            {
                redirectLocally = ParseBoolQueryParameter(httpRequest.Query, "result")
                                    ?? false;

                JobQueueKey queueKey = await GetJobQueueKey(services, httpContext)
                                                .ConfigureAwait(false);

                var jobInput = new PromiseRequest
                {
                    Storage = services.PromiseStorage,
                    RouteKey = routeKey,
                    JobQueueKey = queueKey,
                    OwnerPrincipal = httpContext.User,
                    ContentType = httpRequest.ContentType,
                    ContentLength = httpRequest.ContentLength,
                    PipeReader = httpRequest.BodyReader,
                    FireAndForget = !redirectLocally,
                    CancellationToken = cancellationToken
                };

                promiseId = await executor.Invoke(jobInput)
                                          .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // FIXME remove the Promise object here
                await TranslateExceptionToHttpResponseAsync(httpContext,
                                                            services.ExceptionTranslator, 
                                                            e, 
                                                            httpResponse)
                    .ConfigureAwait(false);
                return;
            }

            if (redirectLocally)
            {
                var timeout = ParseTimeout(httpRequest.Query);

                var parameters = new QueryParameters
                {
                    Timeout = timeout,
                    AcceptedContentTypes = httpRequest.Headers.Accept,
                    AcceptedInnerContentTypes = httpRequest.Headers[HeartyHttpHeaders.AcceptItem],
                    IsPost = true
                };

                IPromiseClientInfo clientInfo = new BasicPromiseClientInfo();

                await RespondToGetPromiseAsync(services, promiseId, httpResponse,
                                               parameters,
                                               clientInfo, cancellationToken)
                                .ConfigureAwait(false);
            }
            else
            {
                httpResponse.StatusCode = StatusCodes.Status303SeeOther;
                httpResponse.Headers.Add(HeartyHttpHeaders.PromiseId, promiseId.ToString());

                httpResponse.Headers.Location =
                    httpRequest.PathBase.Add($"/promises/{promiseId}").ToString();
            }
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
            var services = new Services(endpoints.ServiceProvider);

            // FIXME This should be managed by a cache
            IPromiseClientInfo clientInfo = new BasicPromiseClientInfo();

            return endpoints.MapGet(
                    pattern: "/promises/{serviceId}/{sequenceNumber}",
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
            var services = new Services(endpoints.ServiceProvider);

            return endpoints.MapDelete(
                    "/promises/{serviceId}/{sequenceNumber}",
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
            var services = new Services(endpoints.ServiceProvider);

            return endpoints.MapDelete(
                    "/jobs/{serviceId}/{sequenceNumber}/",
                    httpContext => CancelJobAsync(services, httpContext, kill: true));
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
                Timeout = timeout,
                AcceptedContentTypes = httpRequest.Headers.Accept,
                AcceptedInnerContentTypes = httpRequest.Headers[HeartyHttpHeaders.AcceptItem]
            };

            return RespondToGetPromiseAsync(services, promiseId, httpResponse, 
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
        /// Selects what sub-part of a promise is to be queried
        /// and how to emit it in <see cref="WriteAsHttpResponseAsync" />.
        /// </summary>
        public readonly struct QueryParameters
        {
            /// <summary>
            /// Time interval to (asynchronously) wait when the 
            /// requested promise is not yet completed.
            /// </summary>
            /// <remarks>
            /// A zero timeout means the query returns immediately
            /// indicating an error or no data when the promise
            /// is not yet completed.
            /// </remarks>
            public TimeSpan Timeout { get; init; }

            /// <summary>
            /// The content types that may be returned to the client.
            /// </summary>
            public StringValues AcceptedContentTypes { get; init; }

            /// <summary>
            /// The content types for the items in a container
            /// that may be returned to the client.
            /// </summary>
            public StringValues AcceptedInnerContentTypes { get; init; }

            /// <summary>
            /// Whether the originating HTTP request has the POST method 
            /// or another method that is considered to be non-idempotent
            /// or modifying.
            /// </summary>
            /// <remarks>
            /// This property affects the HTTP headers that are emitted
            /// in response.
            /// </remarks>
            public bool IsPost { get; init; }
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
                var queueKey = await GetJobQueueKey(services, httpContext).ConfigureAwait(false);

                success = services.RemoteCancellation
                                  .TryCancelJobForClient(queueKey, promiseId);
            }

            httpResponse.StatusCode = success ? StatusCodes.Status202Accepted
                                              : StatusCodes.Status404NotFound;
        }

        /// <summary>
        /// Write a short text message as the HTTP response body 
        /// describing the state of the promise or the operation.
        /// </summary>
        private static ValueTask WriteBodyTextMessageAsync(HttpResponse httpResponse, 
                                                           string? brief, 
                                                           string description)
        {
            if (brief is not null)
                httpResponse.Headers.Add("Reason", brief);

            // Encode first to get the length of the body
            var encoding = Encoding.UTF8;
            int length = encoding.GetByteCount(description);
            Span<byte> buffer = (length <= 8192) ? stackalloc byte[length] : new byte[length];
            Encoding.UTF8.GetBytes(description, buffer);

            httpResponse.ContentType = ServedMediaTypes.TextPlain.ToString();
            httpResponse.ContentLength = length;

            var writer = httpResponse.BodyWriter;
            buffer.CopyTo(writer.GetSpan(length));
            writer.Advance(length);
            return writer.CompleteAsync();
        }

        /// <summary>
        /// Write the contents of a <see cref="Promise" /> as a response
        /// to an HTTP request.
        /// </summary>
        /// <remarks>
        /// This function is made public so that applications using the Hearty
        /// framework can supply their own custom endpoints, that may look
        /// up, or create, <see cref="Promise" /> in some other way, but
        /// follow the same "standard" conventions to send its contents over
        /// the HTTP protocol.
        /// </remarks>
        /// <param name="promise">
        /// The desired promise object.  If null, the response will be
        /// HTTP error 404.
        /// </param>
        /// <param name="httpResponse">
        /// Where the HTTP headers and body will be written to.
        /// </param>
        /// <param name="parameters">
        /// Parameters that select what exactly to output from <paramref name="promise" />.
        /// </param>
        /// <param name="client">
        /// Represents the (remote) HTTP client.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be triggered to cancel the response, usually from
        /// <see cref="HttpContext.RequestAborted" />.
        /// </param>
        public static async Task WriteAsHttpResponseAsync(
            this Promise? promise,
            HttpResponse httpResponse,
            QueryParameters parameters,
            IPromiseClientInfo client,
            CancellationToken cancellationToken)
        {
            if (promise is null)
            {
                httpResponse.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (parameters.IsPost)
            {
                httpResponse.Headers[HeartyHttpHeaders.PromiseId]
                    = promise.Id.ToString();
            }

            if (parameters.Timeout == TimeSpan.Zero && !promise.HasOutput)
            {
                httpResponse.StatusCode = StatusCodes.Status202Accepted;

                await WriteBodyTextMessageAsync(
                    httpResponse,
                    "Incomplete promise",
                    "The requested promise exists, but its associated computation has not complete, so no results can be returned. ")
                    .ConfigureAwait(false);
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

            var formatInfo = output.GetFormatInfo(format);

            httpResponse.StatusCode = StatusCodes.Status200OK;
            httpResponse.ContentType = formatInfo.MediaType.ToString();
            httpResponse.ContentLength = output.GetContentLength(format);

            var writeRequest = new PromiseWriteRequest
            {
                Format = format,
                InnerFormat = formatInfo.IsContainer
                                ? parameters.AcceptedInnerContentTypes
                                : StringValues.Empty
            };

            if (!parameters.IsPost)
            {
                httpResponse.Headers.Vary =
                    formatInfo.IsContainer ? "Accept, Accept-Item"
                                           : "Accept";
            }

            await output.WriteToPipeAsync(httpResponse.BodyWriter,
                                          writeRequest,
                                          cancellationToken)
                        .ConfigureAwait(false);

            await httpResponse.BodyWriter.CompleteAsync();
        }


        private static Task RespondToGetPromiseAsync(Services services,
                                                     PromiseId promiseId,
                                                     HttpResponse httpResponse,
                                                     QueryParameters parameters,
                                                     IPromiseClientInfo client,
                                                     CancellationToken cancellationToken)
        {
            var promise = services.PromiseStorage.GetPromiseById(promiseId);
            return WriteAsHttpResponseAsync(promise,
                                                   httpResponse,
                                                   parameters,
                                                   client,
                                                   cancellationToken);
        }
    }
}
