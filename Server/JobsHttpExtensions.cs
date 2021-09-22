using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
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
                           var promise = jobsController.CreatePromise(routeKey, out var id);

                           var jobInput = new JobInput(httpRequest.ContentType,
                                                       httpRequest.ContentLength,
                                                       httpRequest.BodyReader,
                                                       cancellationToken);

                           Job job;
                           
                           try
                           {
                               // FIXME CancellationToken should be separately created for the promise.
                               job = await executor.Invoke(jobInput, promise);
                           }
                           catch (PayloadTooLargeException)
                           {
                               return Results.StatusCode(413);
                           }
                           catch (Exception e)
                           {
                               return Results.Problem(e.ToString(), statusCode: 400);
                           }

                           httpResponse.Headers.Add("x-job-id", id);

                           var backgroundTask = job.Task;
                           if (backgroundTask.IsCompleted)
                           {
                               promise.PostResult(backgroundTask.Result);
                           }
                           else
                           {
                               static async void AwaitAndPostResultAsync(ValueTask<PromiseOutput> backgroundTask, Promise promise)
                               {
                                   var output = await backgroundTask.ConfigureAwait(false);
                                   promise.PostResult(output);
                               }

                               AwaitAndPostResultAsync(backgroundTask, promise);
                           }

                           // URL encoding??
                           return Results.Redirect($"/jobs/v1/current/{id}", permanent: true, preserveMethod: false);
                       });

            endpoints.MapGet("/jobs/v1/current/" + routeKey + "/{**id}",
                    async ([FromRoute] string id,
                           [FromQuery] ExpiryTimeSpan? timeout,
                           CancellationToken cancellationToken,
                           HttpResponse httpResponse) =>
                    {
                        var promise = jobsController.GetPromiseById(id);
                        if (promise == null)
                        {
                            httpResponse.StatusCode = StatusCodes.Status404NotFound;
                            return;
                        }          
                        
                        if (timeout == null && !promise.IsCompleted)
                        {
                            httpResponse.StatusCode = StatusCodes.Status204NoContent;
                            return;
                        }

                        using var result = await promise.GetResultAsync(clientInfo, timeout?.Value, cancellationToken);
                        var output = result.NormalOutput;
                        var pipeReader = await output.GetPipeReaderAsync(output.SuggestedContentType, 0, cancellationToken);

                        httpResponse.StatusCode = StatusCodes.Status200OK;
                        httpResponse.ContentType = output.SuggestedContentType;
                        httpResponse.ContentLength = output.ContentLength;

                        await pipeReader.CopyToAsync(httpResponse.BodyWriter, cancellationToken);
                    });
        }
    }
}
