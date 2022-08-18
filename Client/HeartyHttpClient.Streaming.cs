using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using HttpStatusCode = System.Net.HttpStatusCode;
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
            PayloadReader<T> reader,
            object? context = null)
    {
        if (reader.OwnsStream)
            ThrowOnReaderOwningStream(nameof(reader));

        return new ItemStream<T>(this, reader, promiseId);
    }

    /// <inheritdoc cref="IHeartyClient.RunJobStreamAsync" />
    public IAsyncEnumerable<KeyValuePair<int, T>>
        RunJobStreamAsync<T>(string route,
                             PayloadWriter input,
                             PayloadReader<T> reader,
                             JobQueueKey queue = default,
                             CancellationToken cancellationToken = default,
                             object? context = null)
    {
        if (reader.OwnsStream)
            ThrowOnReaderOwningStream(nameof(reader));

        var url = CreateRequestUrl("requests/",
                                   route: route,
                                   wantResult: true,
                                   queue: queue);

        return new ItemStream<T>(this,
                                 reader,
                                 jobUrl: url, 
                                 jobInput: input.CreateHttpContent(),  
                                 repostOnFailure: true, 
                                 cancellationToken);
    }

    /// <summary>
    /// Add the HTTP headers for content negotiation 
    /// to receive a container of items (for streaming downloads).
    /// </summary>
    private static void AddHeadersForItemStream(HttpRequestMessage request)
    {
        var headers = request.Headers;
        headers.TryAddWithoutValidation(HeaderNames.Accept, MultipartParallelMediaType);
        headers.TryAddWithoutValidation(HeaderNames.Accept, ExceptionPayload.JsonMediaType);
        headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, ExceptionPayload.JsonMediaType);
    }

    /// <summary>
    /// Asynchronously downloads a stream of items from a Hearty server,
    /// posting the job only on the first time it is enumerated.
    /// </summary>
    private sealed class ItemStream<T> : IAsyncEnumerable<KeyValuePair<int, T>>
    {
        /// <summary>
        /// Object shared by multiple calls to <see cref="GetAsyncEnumerator" />
        /// that need to post the job before the items can be enumerated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This member variable is manipulated by atomic compare-and-exchange
        /// operations to allow the concurrent enumeration.  The value stored
        /// here can be of four types:
        /// <list type="bullet">
        /// <item>Null, meaning there is no job that can be re-used yet. </item>
        /// <item><see cref="Task"/>, indicating that the posted job has come
        /// back.  Failure is indicated by an instance wrapping an exception. </item>
        /// <item><see cref="TaskCompletionSource" />, whose <see cref="TaskCompletionSource.Task" />
        /// member can be awaited for the job to be posted.
        /// </item>
        /// <item>The same as <see cref="_jobUrl" />, meaning the job 
        /// has been started, but there is only one awaiter (which can await
        /// the request directly).  This state is an optimization to not
        /// create <see cref="TaskCompletionSource" /> in the very common case
        /// that there is only one awaiter.
        /// </item>
        /// </list>
        /// </para>
        /// </remarks>
        private object? _requestState;

        /// <summary>
        /// The ID of the job, which is set once the job has been
        /// successfully posted.
        /// </summary>
        private PromiseId _promiseId;

        /// <summary>
        /// Accesses <see cref="_promiseId" /> avoiding torn reads.
        /// </summary>
        private PromiseId PromiseId
        {
            get => PromiseId.AtomicRead(ref _promiseId);
            set => PromiseId.AtomicWrite(ref _promiseId, value);
        }

        /// <summary>
        /// The URL to POST the job on the job server, or null if only
        /// an existing job should be retrieved.
        /// </summary>
        private readonly string? _jobUrl;

        /// <summary>
        /// The input body to POST when creating the job on the job server.
        /// </summary>
        private readonly HttpContent? _jobInput;

        /// <summary>
        /// The parent object that created this object.
        /// </summary>
        private readonly HeartyHttpClient _client;

        /// <summary>
        /// Reader passed in from the user to decode the payload of each item.
        /// </summary>
        private readonly PayloadReader<T> _reader;

        /// <summary>
        /// Cancels the POST of the job, when triggered.
        /// </summary>
        private readonly CancellationToken _jobCancellationToken;

        /// <summary>
        /// If true, do not cache failures from posting the job
        /// to the job server, allowing posting to be re-tried.
        /// </summary>
        private readonly bool _repostOnFailure;

        /// <summary>
        /// Construct for reading an existing promise with known ID.
        /// </summary>
        public ItemStream(HeartyHttpClient client,
                          PayloadReader<T> reader,
                          PromiseId promiseId)
        {
            _promiseId = promiseId;
            _reader = reader;
            _client = client;
            _repostOnFailure = false;    // irrelevant
        }

        /// <summary>
        /// Construct for posting a new job.  The promise ID is only
        /// known after the job posting is successful.
        /// </summary>
        public ItemStream(HeartyHttpClient client,
                          PayloadReader<T> reader,
                          string jobUrl,
                          HttpContent jobInput,
                          bool repostOnFailure,
                          CancellationToken jobCancellationToken)
        {
            _jobUrl = jobUrl;
            _jobInput = jobInput;
            _reader = reader;
            _client = client;
            _repostOnFailure = repostOnFailure;
            _jobCancellationToken = jobCancellationToken;
        }

        /// <summary>
        /// Get or create the task to await when the job has already been
        /// posted once.
        /// </summary>
        /// <param name="requestState">
        /// On return, set to the last value of <see cref="_requestState" /> 
        /// that has been read.
        /// </param>
        /// <returns>
        /// The task that completes when the job completes, or null
        /// if there is no job that can be re-used but this method
        /// has just marked one as started.
        /// </returns>
        private Task? GetTaskForStartedJob(out object? requestState)
        {
            object? s = Interlocked.CompareExchange(ref _requestState, _jobUrl, null);

            while (true)
            {
                if (s is null)
                {
                    // No job has started yet, or it has just been reset,
                    // and now it has just been marked as started.
                    requestState = _jobUrl;
                    return null;
                }
                else if (s is Task oldTask)
                {
                    // Job has been posted as complete already.
                    requestState = s;
                    return oldTask;
                }
                else if (s is TaskCompletionSource oldTaskSource)
                {
                    // Job is in progress, and it is already awaited a second time,
                    // through the TaskCompletionSource created from the last
                    // case below.
                    requestState = s;
                    return oldTaskSource.Task;
                }
                else // requestState is _jobUrl
                {
                    // Create TaskCompletionSource to prepare to await the job
                    // for the second time.  When the job posting completes,
                    // the TaskCompletionSource will get completed.
                    var newTaskSource = new TaskCompletionSource();
                    s = Interlocked.CompareExchange(ref _requestState, 
                                                    newTaskSource, 
                                                    _jobUrl);

                    if (object.ReferenceEquals(s, _jobUrl))
                    {
                        requestState = newTaskSource;
                        return newTaskSource.Task;
                    }

                    // If the current call races with another one doing the same,
                    // drop the speculatively-created TaskCompleteSource and retry. 
                    if (s is null)
                        s = Interlocked.CompareExchange(ref _requestState, _jobUrl, null);
                }
            }
        }

        /// <inheritdoc cref="IAsyncEnumerable{T}.GetAsyncEnumerator" />
        public async IAsyncEnumerator<KeyValuePair<int, T>> 
            GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            // The default values here are never read but are needed to avoid
            // false alarms from the C# compiler's definite assignment analysis.
            PromiseId promiseId = default;
            object? oldState = null;

            HttpResponseMessage? response = null;

            if (_jobUrl is not null)
            {
                var requestTask = GetTaskForStartedJob(out oldState);

                // The job is to be posted for the first time.
                if (requestTask is null)
                {
                    try
                    {
                        // Construct REST API message to post job
                        var request = new HttpRequestMessage(HttpMethod.Post, _jobUrl);
                        request.Content = _jobInput;
                        _client.AddAuthorizationHeader(request);
                        AddHeadersForItemStream(request);
                        _reader.AddAcceptHeaders(request.Headers, HeartyHttpHeaders.AcceptItem);

                        response = await _client.SendHttpMessageAsync(request, false, 
                                                                      _jobCancellationToken)
                                                .ConfigureAwait(false);

                        response.EnsureSuccessStatusCode();
                        promiseId = GetPromiseId(response.Headers);

                        PromiseId = promiseId;
                    }
                    catch (Exception e)
                    {
                        // Errors do not stick when _retryOnFailure is true and
                        // the exception is not a (user-triggered) cancellation.
                        requestTask = !_repostOnFailure ||
                                            (e is OperationCanceledException oce &&
                                             oce.CancellationToken == _jobCancellationToken)
                                          ? Task.FromException(e)
                                          : null;

                        oldState = Interlocked.Exchange(ref _requestState, requestTask);
                        if (oldState is TaskCompletionSource promiseIdSourceForFailure)
                            promiseIdSourceForFailure.SetException(e);

                        throw;
                    }

                    // Finalize the state for a successfully posted job.
                    requestTask = Task.CompletedTask;
                    oldState = Interlocked.Exchange(ref _requestState, requestTask);
                    if (oldState is TaskCompletionSource promiseIdSourceForSuccess)
                        promiseIdSourceForSuccess.SetResult();
                    oldState = requestTask;
                }

                // The job has already been posted once.  Wait until that
                // completes to obtain the promise ID.
                else
                {
                    // If posting the job failed, the exception will be re-thrown here.
                    await requestTask.ConfigureAwait(false);

                    // At this point, the property PromiseId is
                    // guaranteed to be valid, because it is set before
                    // the job's task gets completed, on success.
                }
            }

            // If this call did not just post a job above, obtaining a successful
            // response, then issue a HTTP request now to re-download the same data.
            if (response is null)
            {
                promiseId = PromiseId;

                // Construct REST API message to download item stream
                var url = _client.CreateRequestUrl("promises/", promiseId);
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                _client.AddAuthorizationHeader(request);
                AddHeadersForItemStream(request);
                _reader.AddAcceptHeaders(request.Headers, HeartyHttpHeaders.AcceptItem);

                response = await _client.SendHttpMessageAsync(request, false, cancellationToken)
                                        .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound &&
                    _jobUrl is not null && _repostOnFailure)
                {
                    // Try re-submitting the job, the next time this method is called,
                    // if the server does not hold the promise, e.g. if it has
                    // restarted and lost all of its previous data in memory.
                    //
                    // We must compare with oldState here to avoid swapping out the
                    // wrong value if multiple calls to this method race.
                    Interlocked.CompareExchange(ref _requestState, null, oldState);

                    // The following statement will throw an exception.
                }

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
