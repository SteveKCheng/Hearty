using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nerdbank.Streams;
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    public static class JobsHttpExtensions
    {
        public static void MapHttpRoutes(this JobsController jobsController, 
                                         IEndpointRouteBuilder endpoints, 
                                         string routeKey,
                                         JobExecutor executor)
        {
            routeKey = routeKey.Trim('/');

            // FIXME This should be managed by a cache
            IPromiseClientInfo clientInfo = new BasicPromiseClientInfo();

            endpoints.MapPost("/jobs/v1/queue/" + routeKey, 
                       async (HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken) =>
                       {
                           Payload payload;
                           try
                           {
                               payload = await ReadPayloadSafelyAsync(httpRequest, 16 * 1024 * 1024, cancellationToken);
                           }
                           catch (PayloadTooLargeException)
                           {
                               return Results.StatusCode(413);
                           }
                           
                           var promise = jobsController.CreatePromise(routeKey, payload, out var id);
                           httpResponse.Headers.Add("x-job-id", id);

                           Job job;
                           
                           try
                           {
                               // FIXME CancellationToken should be separately created for the promise.
                               job = await executor.Invoke(new JobInput(payload.SuggestedContentType, payload.Body.Length, null!, cancellationToken), promise);
                           }
                           catch (Exception e)
                           {
                               return Results.Problem(e.ToString(), statusCode: 400);
                           }

                           var backgroundTask = job.Task;
                           if (backgroundTask.IsCompleted)
                           {
                               promise.PostResult(backgroundTask.Result.Payload);
                           }
                           else
                           {
                               static async void AwaitAndPostResultAsync(ValueTask<PromiseResult> backgroundTask, Promise promise)
                               {
                                   var result = await backgroundTask.ConfigureAwait(false);
                                   promise.PostResult(result.Payload);
                               }

                               AwaitAndPostResultAsync(backgroundTask, promise);
                           }

                           // URL encoding??
                           return Results.Redirect($"/jobs/v1/current/{id}", permanent: true, preserveMethod: false);
                       });

            endpoints.MapGet("/jobs/v1/current/" + routeKey + "/{**id}",
                       async ([FromRoute] string id,
                              [FromQuery] ExpiryTimeSpan? timeout,
                              CancellationToken cancellationToken) =>
                       {
                           var promise = jobsController.GetPromiseById(id);
                           if (promise == null || (timeout == null && !promise.IsCompleted))
                               return Results.NotFound();

                           using var result = await promise.GetResultAsync(clientInfo, timeout?.Value, cancellationToken);
                           return Results.Stream(new ReadOnlySequence<byte>(result.Payload.Body).AsStream(),
                                                 result.Payload.SuggestedContentType);
                       });
        }

        /// <summary>
        /// Read the body of a HTTP (POST/PUT) request as a sequence of bytes with an associated
        /// media type, applying limits on the amount of data.
        /// </summary>
        private static async Task<Payload>
            ReadPayloadSafelyAsync(HttpRequest httpRequest, int lengthLimit, CancellationToken cancellationToken)
        {
            var headers = httpRequest.Headers;
            var contentType = headers.ContentType.ToString();
            var contentLength = headers.ContentLength;

            if (contentLength > lengthLimit)
                throw new PayloadTooLargeException();

            var payload = new byte[(contentLength + 1) ?? 8092];

            var stream = httpRequest.Body;
            int bytesTotalRead = 0;

            while (true)
            {
                int bytesJustRead = await stream.ReadAsync(new Memory<byte>(payload).Slice(bytesTotalRead),
                                                           cancellationToken);
                if (bytesJustRead == 0)
                    break;

                bytesTotalRead += bytesJustRead;

                if (contentLength != null)
                {
                    if (bytesTotalRead > contentLength.Value)
                        throw new PromiseException("Got more bytes of payload than what the HTTP header Content-Length indicated. ");
                }
                else
                {
                    if (payload.Length - bytesTotalRead < payload.Length / 4)
                    {
                        var newPayload = new byte[payload.Length * 2];
                        payload.AsSpan().Slice(0, bytesTotalRead).CopyTo(newPayload);
                        payload = newPayload;
                    }
                }
            }

            return new Payload(contentType, new Memory<byte>(payload, 0, bytesTotalRead));
        }
    }
}
