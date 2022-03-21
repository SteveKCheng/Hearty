using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
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

        return GetResultStreamInternalAsync(
                null,
                request,
                reader,
                cancellationToken);
    }

    private static async ValueTask<KeyValuePair<int, T>?>
        ReadNextItemFromMultipartReaderAsync<T>(
            PromiseId promiseId,
            MultipartReader multipartReader,
            PayloadReader<T> payloadReader,
            CancellationToken cancellationToken)
    {
        MultipartSection? section =
            await multipartReader.ReadNextSectionAsync(cancellationToken)
                                 .ConfigureAwait(false);

        if (section is null)
            return null;

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

    private async IAsyncEnumerable<KeyValuePair<int, T>>
        GetResultStreamInternalAsync<T>(
            PromiseId? inputPromiseId,
            HttpRequestMessage request,
            PayloadReader<T> reader,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(request,
                                                   HttpCompletionOption.ResponseHeadersRead,
                                                   cancellationToken)
                                        .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var content = response.Content;

        var promiseId = inputPromiseId ??
                        GetPromiseId(response.Headers);

        var boundary = RestApiUtilities.GetMultipartBoundary(
                        content.Headers.TryGetSingleValue(HeaderNames.ContentType));

        var multipartReader = new MultipartReader(boundary,
                                                  content.ReadAsStream(cancellationToken));

        KeyValuePair<int, T>? item;
        while ((item = await ReadNextItemFromMultipartReaderAsync(
                        promiseId,
                        multipartReader,
                        reader,
                        cancellationToken).ConfigureAwait(false))
                        is not null)
        {
            yield return item.GetValueOrDefault();
        }
    }


    /// <inheritdoc cref="IHeartyClient.GetResultStreamAsync" />
    public IAsyncEnumerable<KeyValuePair<int, T>>
        GetResultStreamAsync<T>(
            PromiseId promiseId,
            PayloadReader<T> reader,
            CancellationToken cancellationToken = default)
    {
        if (reader.OwnsStream)
            ThrowOnReaderOwningStream(nameof(reader));

        var url = CreateRequestUrl("jobs/v1/id/", promiseId);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request);
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, MultipartParallelMediaType);
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, ExceptionPayload.JsonMediaType);
        request.Headers.TryAddWithoutValidation(HeartyHttpHeaders.AcceptItem, ExceptionPayload.JsonMediaType);
        reader.AddAcceptHeaders(request.Headers, HeartyHttpHeaders.AcceptItem);

        return GetResultStreamInternalAsync(
                promiseId,
                request,
                reader,
                cancellationToken);
    }

}
