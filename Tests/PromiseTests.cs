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
using Hearty.Server.FasterKV;
using System.Buffers.Text;

namespace Hearty.Tests;

public sealed class PromiseTests
{
    private readonly ILogger<BasicPromiseStorage> _logger;
    private readonly ILogger<FasterDbPromiseStorage> _logger2;

    public PromiseTests(ITestOutputHelper testOutput)
    {
        _logger = testOutput.BuildLoggerFor<BasicPromiseStorage>();
        _logger2 = testOutput.BuildLoggerFor<FasterDbPromiseStorage>();
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

    private static async Task<PromiseData?> ObserveRepeatedlyAsync(Promise promise,
                                                                   IPromiseClientInfo client,
                                                                   TimeSpan? timeout,
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

    private async Task CreateAndFulfillPromiseAsync(PromiseStorage promiseStorage, IPromiseClientInfo client, TimeSpan timeout)
    {
        var promise = promiseStorage.CreatePromise(null)!;
        Assert.NotNull(promise);
        var cancelSource = new CancellationTokenSource();

        // Simulate parallel observers
        var tasks = new Task<PromiseData?>[128];
        for (int i = 0; i < tasks.Length; ++i)
            tasks[i] = Task.Run(() => ObserveRepeatedlyAsync(promise, client, timeout, cancelSource.Token), cancelSource.Token);

        // Post result
        static async ValueTask<PromiseData> WaitAndReturnResultAsync(TimeSpan timeout, PromiseData data)
        {
            await Task.Delay(timeout);
            return data;
        }

        var payload = GenerateRandomPayload();

        var wait = TimeSpan.FromSeconds(2);
        promise.AwaitAndPostResult(WaitAndReturnResultAsync(wait, payload), BasicExceptionTranslator.Instance);

        await Task.Delay(wait + TimeSpan.FromSeconds(3));
        cancelSource.Cancel();

        var outputs = await Task.WhenAll(tasks);
        Assert.All(outputs, output => Assert.Equal(payload, output));
    }

    private static Payload GenerateRandomPayload(int size = 8192)
    {
        var buffer = new byte[size];
        Random.Shared.NextBytes(buffer);
        for (int i = 0; i < size; ++i)
        {
            ref byte b = ref buffer[i];
            b = Math.Max((byte)(b & 0x7F), (byte)0x20);
        }

        return new Payload(ServedMediaTypes.TextPlain, buffer, isFailure: false);
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

    [Fact]
    public async Task CreateAndFulfullPromises2()
    {
        using var promiseStorage = new FasterDbPromiseStorage(_logger2, new PromiseDataSchemas(), new FasterDbFileOptions
        {
            Preallocate = false,
            DeleteOnDispose = true,
            HashIndexSize = 16,
            PageLog2Size = 18,          // 256 KB
            MemoryLog2Capacity = 24     // 16 MB
        });
            
        var client = new DummyClient();
        var timeout = TimeSpan.FromSeconds(1);

        await Parallel.ForEachAsync(Enumerable.Range(0, 128), new ParallelOptions
        {
            MaxDegreeOfParallelism = 32,
        }, async (i, cancellationToken) =>
        {
            await CreateAndFulfillPromiseAsync(promiseStorage, client, timeout);
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50)), cancellationToken);
        });
    }

    [Fact]
    public async Task FulfillFasterDbPromises()
    {
        using var promiseStorage = new FasterDbPromiseStorage(_logger2, new PromiseDataSchemas(), new FasterDbFileOptions
        {
            Preallocate = false,
            DeleteOnDispose = true,
            HashIndexSize = 1 << 18,
            PageLog2Size = 18,          // 256 KB
            MemoryLog2Capacity = 29     // 512 MB
        });

        int promiseCount = 3200;
        var promiseIds = new List<PromiseId>(promiseCount);
        for (int i = 0; i < promiseCount; ++i)
        {
            var promise = promiseStorage.CreatePromise(null);
            promise.AwaitAndPostResult(ValueTask.FromResult<PromiseData>(GenerateRandomPayload(32)), BasicExceptionTranslator.Instance);
            promiseIds.Add(promise.Id);
        }

        GC.Collect();

        var client = new DummyClient();
        var timeout = TimeSpan.FromSeconds(1);

        await Parallel.ForEachAsync(Enumerable.Range(0, promiseCount), new ParallelOptions
        {
            MaxDegreeOfParallelism = 32
        }, async (k, cancellationToken) =>
        {
            // Simulate parallel observers
            var tasks = new Task<PromiseData?>[128];
            var cancelSource = new CancellationTokenSource();
            for (int i = 0; i < tasks.Length; ++i)
            {
                var promise = promiseStorage.GetPromiseById(promiseIds[k])!;
                Assert.NotNull(promise);
                tasks[i] = Task.Run(() => ObserveRepeatedlyAsync(promise, client, timeout, cancelSource.Token), cancelSource.Token);
            }

            cancelSource.CancelAfter(TimeSpan.FromMilliseconds(Random.Shared.Next(250)));

            var outputs = await Task.WhenAll(tasks);
            //Assert.All(outputs, output => Assert.NotNull(output));
        });
    }
}
