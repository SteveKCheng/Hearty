using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using Microsoft.Net.Http.Headers;
using Microsoft.AspNetCore.WebUtilities;
using Hearty.Common;

namespace Hearty.Client;

public partial class HeartyHttpClient
{
    private static void ThrowOnReaderOwningStream(string paramName)
    {
        throw new ArgumentException("The payload reader on each item cannot own the underlying stream. ",
                                    paramName);
    }

    /// <inheritdoc cref="IHeartyClient.GetResultStreamAsync" />
    public IAsyncEnumerable<KeyValuePair<int, T>>
        GetResultStreamAsync<T>(
            PromiseId promiseId,
            PayloadReader<T> reader)
    {
        if (reader.OwnsStream)
            ThrowOnReaderOwningStream(nameof(reader));

        return new ItemStream<T>(this, promiseId, reader);
    }

    /// <inheritdoc cref="IHeartyClient.RunJobStreamAsync" />
    public IAsyncEnumerable<KeyValuePair<int, T>>
        RunJobStreamAsync<T>(string route,
                             PayloadWriter input,
                             PayloadReader<T> reader,
                             JobQueueKey queue = default,
                             CancellationToken cancellationToken = default)
    {
        if (reader.OwnsStream)
            ThrowOnReaderOwningStream(nameof(reader));

        var url = CreateRequestUrl("jobs/v1/queue",
                                   route: route,
                                   wantResult: true,
                                   queue: queue);

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuthorizationHeader(request);
        request.Content = input.CreateHttpContent();
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, MultipartParallelMediaType);
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, ExceptionPayload.JsonMediaType);
        request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, ExceptionPayload.JsonMediaType);
        reader.AddAcceptHeaders(request.Headers, HeartyHttpHeaders.AcceptItem);

        return new ItemStream<T>(this, request, reader, cancellationToken);
    }

    /// <summary>
    /// Asynchronously downloads a stream of items from a Hearty server,
    /// posting the job only on the first time it is enumerated.
    /// </summary>
    private sealed class ItemStream<T> : IAsyncEnumerable<KeyValuePair<int, T>>
    {
        private object? _requestState;
        private PromiseId _promiseId;
        private readonly HttpRequestMessage? _jobRequest;
        private readonly HeartyHttpClient _client;
        private readonly PayloadReader<T> _reader;
        private readonly CancellationToken _jobCancellationToken;

        /// <summary>
        /// Construct for reading an existing promise with known ID.
        /// </summary>
        public ItemStream(HeartyHttpClient client,
                          PromiseId promiseId, 
                          PayloadReader<T> reader)
        {
            _promiseId = promiseId;
            _reader = reader;
            _client = client;
        }

        /// <summary>
        /// Construct for posting a new job.  The promise ID is only
        /// known after the job posting is successful.
        /// </summary>
        public ItemStream(HeartyHttpClient client,
                          HttpRequestMessage jobRequest,
                          PayloadReader<T> reader,
                          CancellationToken jobCancellationToken)
        {
            _jobRequest = jobRequest;
            _reader = reader;
            _client = client;
            _jobCancellationToken = jobCancellationToken;
        }

        /// <summary>
        /// Get or create the task to await when the job has already been
        /// posted once.
        /// </summary>
        private Task GetTaskForStartedJob(object oldState)
        {
            while (true)
            {
                if (oldState is Task oldTask)
                {
                    return oldTask;
                }
                else if (oldState is TaskCompletionSource oldTaskSource)
                {
                    return oldTaskSource.Task;
                }
                else
                {
                    var promiseIdSource = new TaskCompletionSource();
                    oldState = Interlocked.CompareExchange(ref _requestState, promiseIdSource, _jobRequest)!;
                    if (object.ReferenceEquals(oldState, _jobRequest))
                        return promiseIdSource.Task;
                }
            }
        }

        /// <inheritdoc cref="IAsyncEnumerable{T}.GetAsyncEnumerator" />
        public async IAsyncEnumerator<KeyValuePair<int, T>> 
            GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            // This default value is never read but is needed to avoid a
            // false alarm from the C# compiler's definite assignment analysis.
            PromiseId promiseId = default;  

            HttpResponseMessage? response = null;

            var httpClient = _client._httpClient;
            var jobRequest = _jobRequest;

            if (jobRequest is not null)
            {
                var oldState = Interlocked.CompareExchange(ref _requestState, jobRequest, null);

                // The job is to be posted for the first time.
                if (oldState is null)
                {
                    try
                    {
                        response = await httpClient.SendAsync(jobRequest,
                                                              HttpCompletionOption.ResponseHeadersRead,
                                                              _jobCancellationToken)
                                                   .ConfigureAwait(false);

                        response.EnsureSuccessStatusCode();
                        promiseId = GetPromiseId(response.Headers);

                        _promiseId = promiseId;
                    }
                    catch (Exception e)
                    {
                        oldState = Interlocked.Exchange(ref _requestState, Task.FromException(e));
                        if (oldState is TaskCompletionSource promiseIdSourceForFailure)
                            promiseIdSourceForFailure.SetException(e);
                        throw;
                    }

                    // Finalize the state for a successfully posted job.
                    oldState = Interlocked.Exchange(ref _requestState, Task.CompletedTask);
                    if (oldState is TaskCompletionSource promiseIdSourceForSuccess)
                        promiseIdSourceForSuccess.SetResult();
                }

                // The job has already been posted once.  Wait until that
                // completes to obtain the promise ID.
                else
                {
                    // If posting the job failed, the exception will be re-thrown.
                    await GetTaskForStartedJob(oldState).ConfigureAwait(false);
                }
            }

            // If not posting a job, and thus the promise ID is known,
            // issue HTTP request to download the stream.
            if (response is null)
            {
                promiseId = _promiseId;
                var url = _client.CreateRequestUrl("jobs/v1/id/", promiseId);

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                _client.AddAuthorizationHeader(request);
                request.Headers.TryAddWithoutValidation(HeaderNames.Accept, MultipartParallelMediaType);
                request.Headers.TryAddWithoutValidation(HeaderNames.Accept, ExceptionPayload.JsonMediaType);
                request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, ExceptionPayload.JsonMediaType);
                _reader.AddAcceptHeaders(request.Headers, HeartyHttpHeaders.AcceptItem);

                response = await httpClient.SendAsync(request,
                                                      HttpCompletionOption.ResponseHeadersRead,
                                                      cancellationToken)
                                           .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }

            //
            // Begin decoding multi-part boundaries
            // 

            var content = response.Content;
            var boundary = RestApiUtilities.GetMultipartBoundary(
                            content.Headers.TryGetSingleValue(HeaderNames.ContentType));

            var multipartReader = new MultipartReader(boundary,
                                                      content.ReadAsStream(cancellationToken));

            //
            // Decode multi-part payload, yielding each item in the stream
            //

            MultipartSection? section;

            while ((section = await multipartReader.ReadNextSectionAsync(cancellationToken)
                                                   .ConfigureAwait(false)) is not null)
            {
                var item = await ReadItemFromMultipartSectionAsync(
                                    promiseId,
                                    section,
                                    _reader,
                                    cancellationToken).ConfigureAwait(false);

                yield return item;
            }
        }

        /// <summary>
        /// Decode the payload from one part of a multi-part stream.
        /// </summary>
        private static async ValueTask<KeyValuePair<int, T>>
            ReadItemFromMultipartSectionAsync(
                PromiseId promiseId,
                MultipartSection section,
                PayloadReader<T> payloadReader,
                CancellationToken cancellationToken)
        {
            var ordinalString = section.Headers![HeartyHttpHeaders.Ordinal];
            if (ordinalString.Count != 1)
                throw new InvalidDataException("The 'Ordinal' header is expected in an item in the multi-part message but is not found. ");

            var contentType = new ParsedContentType(
                                section.ContentType
                                ?? throw new InvalidDataException(
                                    "The 'Content-Type' header is missing for an item " +
                                    "in the multi-part message. "));

            // Is an exception
            if (string.Equals(ordinalString[0], "Trailer"))
            {
                var payload = await ExceptionPayload.TryReadAsync(promiseId,
                                                                  contentType,
                                                                  section.Body,
                                                                  cancellationToken)
                                                    .ConfigureAwait(false);
                if (payload is null)
                {
                    throw new InvalidDataException(
                        "The format of a trailer object from the Hearty server " +
                        "is expected to be ExceptionPayload, but is not. ");
                }

                throw payload.ToException();
            }

            if (!int.TryParse(ordinalString[0], out int ordinal))
                throw new InvalidDataException("The 'Ordinal' header is in an item in the multi-part message is invalid. ");

            var promiseIdString = section.Headers![HeartyHttpHeaders.PromiseId];
            if (promiseIdString.Count != 1 ||
                !PromiseId.TryParse(promiseIdString[0], out var itemPromiseId))
            {
                throw new InvalidDataException("The server did not report the Promise ID of the item in a multi-part message properly. ");
            }

            T item = await payloadReader.ReadFromStreamAsync(itemPromiseId,
                                                             contentType,
                                                             section.Body,
                                                             cancellationToken)
                                        .ConfigureAwait(false);

            return KeyValuePair.Create(ordinal, item);
        }
    }
}
