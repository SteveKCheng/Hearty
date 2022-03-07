using Hearty.Common;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Client;

/// <summary>
/// A strongly-typed interface for clients 
/// to access remote promises under the Hearty framework.
/// </summary>
public interface IHeartyClient : IDisposable
{
    /// <summary>
    /// Post a job for the Hearty server to queue up and launch.
    /// </summary>
    /// <param name="route">
    /// The route on the Hearty server to post the job to.
    /// The choices and meanings for this string depends on the
    /// application-level customization of the Hearty server.
    /// </param>
    /// <param name="input">
    /// The input data for the job.  The interpretation of the
    /// data depends on <paramref name="route" /> and the
    /// application-level customization of the Hearty server.
    /// </param>
    /// <param name="queue">
    /// Specifies which queue that the job should belong to.
    /// The choice will be validated by the server.
    /// </param>
    /// <param name="cancellationToken">
    /// Can be triggered to cancel the operation of posting
    /// the job. Note, however, if cancellation races with
    /// successful posting of the job, the job is not cancelled.
    /// </param>
    /// <returns>
    /// ID of the remote promise which is used by the server
    /// to uniquely identify the job.
    /// </returns>
    Task<PromiseId> PostJobAsync(
        string route,
        PayloadWriter input,
        JobQueueKey queue = default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queue up a job for the Hearty server, and return results
    /// when it completes.
    /// </summary>
    /// <typeparam name="T">
    /// The type of object the result should be expressed as.
    /// </typeparam>
    /// <param name="route">
    /// The route on the Hearty server to post the job to.
    /// The choices and meanings for this string depends on the
    /// application-level customization of the Hearty server.
    /// </param>
    /// <param name="reader">
    /// De-serializes the response stream into the desired
    /// object of type <typeparamref name="T" />.
    /// </param>
    /// <param name="input">
    /// The input data for the job.  The interpretation of the
    /// data depends on <paramref name="route" /> and the
    /// application-level customization of the Hearty server.
    /// </param>
    /// <param name="queue">
    /// Specifies which queue that the job should belong to.
    /// The choice will be validated by the server.
    /// </param>
    /// <param name="cancellationToken">
    /// Can be triggered to cancel the operation of posting
    /// the job. Note, however, if cancellation races with
    /// successful posting of the job, the job is not cancelled.
    /// </param>
    /// <returns>
    /// The de-serialized result from the job if it completes
    /// successfully.
    /// </returns>
    Task<T> RunJobAsync<T>(
        string route,
        PayloadWriter input,
        PayloadReader<T> reader,
        JobQueueKey queue = default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for and obtain the output from a remote promise,
    /// as a byte stream.
    /// </summary>
    /// <param name="promiseId">The ID of the desired promise on the
    /// Hearty server.
    /// </param>
    /// <param name="contentTypes">
    /// A list of IANA media types, with optional quality values,
    /// that the Hearty server may return.
    /// </param>
    /// <param name="timeout">
    /// Directs this method to stop waiting if the 
    /// the server does not make the result available by this
    /// time interval.
    /// </param>
    /// <param name="throwOnException">
    /// If true, a promise result that is an exception will
    /// be thrown out from this method.  If false, the
    /// exceptional payload as a byte stream exactly
    /// as received from the server.
    /// </param>
    /// <param name="cancellation">
    /// Can be triggered to cancel the request.
    /// </param>
    /// <returns>
    /// Asynchronous task providing the output 
    /// from the remote promise.
    /// </returns>
    Task<PromiseByteStream> GetPayloadAsync(
        PromiseId promiseId,
        StringValues contentTypes,
        TimeSpan timeout,
        bool throwOnException = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for and obtain the result contained by a remote promise.
    /// </summary>
    /// <typeparam name="T">
    /// The type of object the payload will be de-serialized to.
    /// </typeparam>
    /// <param name="promiseId">The ID of the desired promise on the
    /// server.
    /// </param>
    /// <param name="reader">
    /// Translates the payload of each item into the desired
    /// object of type <typeparamref name="T" />.
    /// </param>
    /// <param name="timeout">
    /// Directs this method to stop waiting if the 
    /// the server does not make the result available by this
    /// time interval.
    /// </param>
    /// <param name="cancellation">
    /// Can be triggered to cancel the request.
    /// </param>
    /// <returns>
    /// Forward-only read-only stream providing the bytes of 
    /// the desired result.
    /// </returns>
    Task<T> GetResultAsync<T>(
        PromiseId promiseId,
        PayloadReader<T> reader,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a stream of items from a promise/job stored
    /// on the Hearty server.
    /// </summary>
    /// <typeparam name="T">
    /// The type of object the payload will be de-serialized to.
    /// </typeparam>
    /// <param name="promiseId">
    /// The ID of the promise or job on the Hearty server
    /// whose content is a stream of items.
    /// </param>
    /// <param name="reader">
    /// De-serializes the payload of each item into the desired
    /// object of type <typeparamref name="T" />.
    /// </param>
    /// <param name="cancellationToken">
    /// Can be triggered to cancel the entire downloading
    /// operation.
    /// </param>
    /// <returns>
    /// Asynchronous task returning the stream of items, once
    /// the streaming connection to the server for the desired
    /// promise has been established.  The stream itself is 
    /// asynchronous, as it will be incrementally downloading
    /// items.  The server may be also be producing
    /// the items concurrently, so that it also cannot make
    /// them available immediately.  The stream may be enumerated
    /// only once as it is not buffered.
    /// </returns>
    Task<IAsyncEnumerable<KeyValuePair<int, T>>> GetResultStreamAsync<T>(
        PromiseId promiseId,
        PayloadReader<T> reader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queue up a job for the Hearty server, and return a
    /// stream of results.
    /// </summary>
    /// <typeparam name="T">
    /// The type of object the result should be expressed as.
    /// </typeparam>
    /// <param name="route">
    /// The route on the Hearty server to post the job to.
    /// The choices and meanings for this string depends on the
    /// application-level customization of the Hearty server.
    /// </param>
    /// <param name="reader">
    /// De-serializes the response stream into the desired
    /// object of type <typeparamref name="T" />.
    /// </param>
    /// <param name="input">
    /// The input data for the job.  The interpretation of the
    /// data depends on <paramref name="route" /> and the
    /// application-level customization of the Hearty server.
    /// </param>
    /// <param name="queue">
    /// Specifies which queue that the job should belong to.
    /// The choice will be validated by the server.
    /// </param>
    /// <param name="cancellationToken">
    /// Can be triggered to cancel the operation of posting
    /// the job. Note, however, if cancellation races with
    /// successful posting of the job, the job is not cancelled.
    /// </param>
    /// <returns>
    /// Asynchronous task returning the stream of items, once
    /// the streaming connection to the server for the desired
    /// promise has been established.  The stream itself is 
    /// asynchronous, as it will be incrementally downloading
    /// items.  The server may be also be producing
    /// the items concurrently, so that it also cannot make
    /// them available immediately.  The stream may be enumerated
    /// only once as it is not buffered.
    /// </returns>
    Task<IAsyncEnumerable<KeyValuePair<int, T>>> RunJobStreamAsync<T>(
        string route,
        PayloadWriter input,
        PayloadReader<T> reader,
        JobQueueKey queue = default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Request a job on the Hearty server 
    /// be cancelled on behalf of this client.
    /// </summary>
    /// <remarks>
    /// If the job is currently being shared by other clients,
    /// it is not interrupted unless all other clients 
    /// also relinquish their interest in their job, 
    /// by cancellation.
    /// </remarks>
    /// <param name="promiseId">The ID of the job to cancel. </param>
    /// <param name="queue">The queue that the job has been
    /// pushed into, for the current client.  This argument
    /// is used to identify a specific instance of the job
    /// if the client has pushed it onto multiple queues.
    /// </param>
    /// <param name="priority">
    /// The priority of that existing job.  This argument
    /// is used to identify a specific instance of the job
    /// if the client has pushed it for multiple priorities.
    /// </param>
    /// <returns>
    /// Asynchronous task that completes when the server
    /// acknowledges the request to cancel the job.
    /// </returns>
    Task CancelJobAsync(
        PromiseId promiseId,
        JobQueueKey queue);

    /// <summary>
    /// Stop a job on the Hearty server for all clients, 
    /// causing it to return a "cancelled" result.
    /// </summary>
    /// <remarks>
    /// This operation typically requires administrator-level
    /// authorization on the Hearty server.  As stopping a job
    /// is implemented cooperatively, even after this method
    /// returns asynchronously, the job may not have actually
    /// stopped yet.
    /// </remarks>
    /// <param name="promiseId">The ID of the job to kill. </param>
    /// <returns>
    /// Asynchronous task that completes when the server
    /// acknowledges the request to stop the job.
    /// </returns>
    Task KillJobAsync(PromiseId promiseId);
}
