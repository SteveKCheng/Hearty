using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Hearty.Server;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using System.Security.Claims;
using System.Threading;

namespace Hearty.Tests;

public sealed class PromiseTests
{
    private readonly ILogger<BasicPromiseStorage> _logger;

    public PromiseTests(ITestOutputHelper testOutput)
    {
        _logger = testOutput.BuildLoggerFor<BasicPromiseStorage>();
    }

    private class DummyClient : IPromiseClientInfo
    {
        public string UserName => String.Empty;

        public ClaimsPrincipal? User => null;

        private uint _count;

        public uint OnSubscribe(Subscription subscription)
        {
            return Interlocked.Increment(ref _count);
        }

        public void OnUnsubscribe(Subscription subscription, uint index)
        {
        }
    }

    private async Task CreateAndFulfillPromiseAsync(PromiseStorage promiseStorage, IPromiseClientInfo client, TimeSpan timeout)
    {
        var promise = promiseStorage.CreatePromise(null)!;
        Assert.NotNull(promise);
        var cancelSource = new CancellationTokenSource();

        static async Task<PromiseData?> ObserveRepeatedlyAsync(Promise promise,
                                                               IPromiseClientInfo client,
                                                               TimeSpan timeout,
                                                               CancellationToken cancellationToken)
        {
            PromiseData? output = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Add random lag
                    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(200)), cancellationToken);

                    using var result = await promise.GetResultAsync(client, timeout, cancellationToken);
                    output = result.Output;
                }
                catch (OperationCanceledException e) when (e.CancellationToken == cancellationToken)
                {
                }
            }

            return output;
        }

        var tasks = new Task<PromiseData?>[128];

        // Simulate parallel observers
        for (int i = 0; i < tasks.Length; ++i)
        {
            tasks[i] = Task.Run(() => ObserveRepeatedlyAsync(promise, client, timeout, cancelSource.Token), cancelSource.Token);
        }

        // Post result
        static async ValueTask<PromiseData> WaitAndReturnResultAsync(TimeSpan timeout, PromiseData data)
        {
            await Task.Delay(timeout);
            return data;
        }

        var payload = new Payload(ServedMediaTypes.TextPlain,
                                  Encoding.UTF8.GetBytes("Proposed Temporary Accommodations"),
                                  isFailure: false);

        var wait = TimeSpan.FromSeconds(2);
        promise.AwaitAndPostResult(WaitAndReturnResultAsync(wait, payload), BasicExceptionTranslator.Instance);

        await Task.Delay(wait + TimeSpan.FromSeconds(3));
        cancelSource.Cancel();

        var outputs = await Task.WhenAll(tasks);
        Assert.All(outputs, output => Assert.Equal(payload, output));
    }

    [Fact]
    public async Task CreateAndFulfullPromises()
    {
        var promiseStorage = new BasicPromiseStorage(_logger);
        var client = new DummyClient();
        var timeout = TimeSpan.FromSeconds(1);

        // Stress test
        var tasks = new Task[1024];

        for (int i = 0; i < tasks.Length; ++i)
        {
            tasks[i] = Task.Run(() => CreateAndFulfillPromiseAsync(promiseStorage, client, timeout));
        }

        await Task.WhenAll(tasks);
    }
}
