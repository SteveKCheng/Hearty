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
/// <remarks>
/// <para>
/// All the methods in this interface take an optional "context"
/// argument to facilitate tracing and logging.  What this object
/// is may be established by convention in implementations. 
/// Alternatively, implementations may offer hooks (delegates)
/// for clients to specify the intepretation of the context
/// object or the action to take with it.  
/// </para>
/// <para>
/// In most frameworks, the per-method state for tracing and logging
/// is propagated non-intrusively through (hidden) async-local variables.
/// However, async-local state is far from straightforward to work with,
/// especially in the situation of <see cref="IAsyncEnumerable{T}" />
/// results returned by major methods in this interface.  
/// The items from <see cref="IAsyncEnumerable{T}" /> are usually,
/// and quite intentionally, lazily retrieved upon 
/// calling <see cref="IAsyncEnumerator{T}.MoveNextAsync" />
/// on the return value of a method call from this interface. 
/// But that means the retrieval operation would not become
/// part of the implicit asynchronous flow of the original method.
/// </para>
/// <para>
/// To avoid that difficulty, this interface specifies that per-method
/// to state to be passed down explicitly, at the expense of making
/// this interface less nice.
/// </para>
/// </remarks>
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
    /// <param name="context">
    /// Object that represents or describes an implementation-defined
    /// context for this operation, to faciliate tracing and logging.
    /// </param>
    /// <returns>
    /// ID of the remote promise which is used by the server
    /// to uniquely identify the job.
    /// </returns>
    Task<PromiseId> PostJobAsync(
        string route,
        PayloadWriter input,
        JobQueueKey queue = default,
        CancellationToken cancellationToken = default,
        object? context = null);

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
    /// <param name="context">
    /// Object that represents or describes an implementation-defined
    /// context for this operation, to faciliate tracing and logging.
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
        CancellationToken cancellationToken = default,
        object? context = null);

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
    /// <param name="cancellationToken">
    /// Can be triggered to cancel the request.
    /// </param>
    /// <param name="context">
    /// Object that represents or describes an implementation-defined
    /// context for this operation, to faciliate tracing and logging.
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
        CancellationToken cancellationToken = default,
        object? context = null);

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
    /// <param name="cancellationToken">
    /// Can be triggered to cancel the request.
    /// </param>
    /// <param name="context">
    /// Object that represents or describes an implementation-defined
    /// context for this operation, to faciliate tracing and logging.
    /// </param>
    /// <returns>
    /// Forward-only read-only stream providing the bytes of 
    /// the desired result.
    /// </returns>
    Task<T> GetResultAsync<T>(
        PromiseId promiseId,
        PayloadReader<T> reader,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        object? context = null);

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
    /// <param name="context">
    /// Object that represents or describes an implementation-defined
    /// context for this operation, to faciliate tracing and logging.
    /// </param>
    /// <returns>
    /// Asynchronous stream of items, which are downloaded
    /// incrementally.  The server may be producing
    /// the items concurrently, so that it also cannot make
    /// them available immediately.  The stream cannot be
    /// assumed to be buffered, so if enumerating it more than once
    /// may entail sending a new download request to the server.
    /// </returns>
    IAsyncEnumerable<KeyValuePair<int, T>> GetResultStreamAsync<T>(
        PromiseId promiseId,
        PayloadReader<T> reader,
        object? context = null);

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
    /// This cancellation token may be separate from the one
    /// passed into <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)" />;
    /// the latter cancels only the downloading of the results.
    /// A job that is successfully cancelled will, of course, 
    /// cause cancelled results to appear in the concurrently
    /// downloaded result stream.
    /// </param>
    /// <param name="context">
    /// Object that represents or describes an implementation-defined
    /// context for this operation, to faciliate tracing and logging.
    /// </param>
    /// <returns>
    /// Asynchronous stream of items from the job, which 
    /// are downloaded incrementally and may be produced
    /// by the server incrementally.  The job is submitted
    /// upon the first time the stream is enumerated.  
    /// Subsequent enumerations will not re-submit the job,
    /// but may entail re-downloading results from the server.
    /// </returns>
    /// <remarks>
    /// <para>
    /// To start a job immediately but only download the results
    /// later, call <see cref="PostJobAsync" /> instead, then
    /// pass in the <see cref="PromiseId" /> returned into
    /// <see cref="GetResultStreamAsync{T}" />.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<KeyValuePair<int, T>> RunJobStreamAsync<T>(
        string route,
        PayloadWriter input,
        PayloadReader<T> reader,
        JobQueueKey queue = default,
        CancellationToken cancellationToken = default,
        object? context = null);

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
    /// <param name="context">
    /// Object that represents or describes an implementation-defined
    /// context for this operation, to faciliate tracing and logging.
    /// </param>
    /// <returns>
    /// Asynchronous task that completes when the server
    /// acknowledges the request to cancel the job.
    /// </returns>
    Task CancelJobAsync(
        PromiseId promiseId,
        JobQueueKey queue, 
        object? context = null);

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
    /// <param name="context">
    /// Object that represents or describes an implementation-defined
    /// context for this operation, to faciliate tracing and logging.
    /// </param>
    /// <returns>
    /// Asynchronous task that completes when the server
    /// acknowledges the request to stop the job.
    /// </returns>
    Task KillJobAsync(PromiseId promiseId,
                      object? context = null);
}
