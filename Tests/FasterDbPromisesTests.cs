using Hearty.Server;
using Hearty.Server.FasterKV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Hearty.Tests;

public class FasterDbPromisesTests
{
    private readonly PromiseDataSchemas _schemas;

    public FasterDbPromisesTests()
    {
        var builder = PromiseDataSchemas.CreateBuilder();
        builder.Add(Payload.SchemaCode, Payload.Deserialize);
        _schemas = new PromiseDataSchemas(builder);
    }

    private Payload GenerateRandomPayload(Random random, int maxSize)
    {
        int numBytes = random.Next(maxSize);
        var bytes = new byte[numBytes];
        random.NextBytes(bytes);

        return new Payload(ServedMediaTypes.Json,
                           new ReadOnlyMemory<byte>(bytes),
                           isFailure: false);
    }

    [Fact]
    public void StoreManyPromises()
    {
        int count = 256;

        using var db = new FasterDbPromiseStorage(_schemas, new FasterDbFileOptions
        {
            Preallocate = false,
            DeleteOnDispose = true,
            HashIndexSize = count,

            PageLog2Size = 18,          // 256 KB
            MemoryLog2Capacity = 24     // 16 MB
        });

        var random = new Random(Seed: 89);

        int payloadMaxSize = 16384;

        var ids = new List<PromiseId>();

        for (int i = 0; i < count; ++i)
        {
            var inputPayload = GenerateRandomPayload(random, payloadMaxSize);
            var outputPayload = GenerateRandomPayload(random, payloadMaxSize);
            var promise = db.CreatePromise(inputPayload, outputPayload);
            ids.Add(promise.Id);
        }

        long entriesCount = db.GetDatabaseEntriesCount();
        Assert.Equal(count, entriesCount);

        // Now all the promise objects should be gone
        GC.Collect();

        // Check we can materialize them all back
        foreach (var id in ids)
        {
            var promise = db.GetPromiseById(id);
            Assert.NotNull(promise);
            Assert.NotNull(promise!.RequestOutput);
            Assert.NotNull(promise!.ResultOutput);
        }
    }
}
