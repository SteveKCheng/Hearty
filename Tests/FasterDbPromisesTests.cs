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
    private readonly PromiseDataSchemas _schemas 
        = new PromiseDataSchemas(PromiseDataSchemas.CreateBuilderWithDefaults());

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
    
    // PromiseList cannot be stored immediately in serialized form.
    // This test ensures later updates to the promise work.
    [Fact]
    public void StorePromiseList()
    {
        int count = 16;

        using var db = new FasterDbPromiseStorage(_schemas, new FasterDbFileOptions
        {
            Preallocate = false,
            DeleteOnDispose = true,
            HashIndexSize = count,

            PageLog2Size = 18,          // 256 KB
            MemoryLog2Capacity = 24     // 16 MB
        });

        // Put this code inside a function so that there is no
        // more reference to the Promise objects (in IL) when
        // this function exits.
        PromiseId CreatePromiseList(PromiseStorage storage, 
                                    int count, 
                                    out IReadOnlyList<PromiseId> members)
        {
            var random = new Random(Seed: 89);
            int payloadMaxSize = 2048;

            var promiseList = new PromiseList(storage);
            var id = storage.CreatePromise(null, promiseList).Id;

            IPromiseListBuilder builder = promiseList;
            var membersList = new List<PromiseId>(capacity: count);

            for (int i = 0; i < count; ++i)
            {
                var outputPayload = GenerateRandomPayload(random, payloadMaxSize);
                var promise = storage.CreatePromise(null, outputPayload);
                builder.SetMember(i, promise);
                membersList.Add(promise.Id);
            }

            Assert.False(promiseList.IsComplete);
            builder.TryComplete(count);
            Assert.True(promiseList.IsComplete);

            members = membersList;
            return id;
        }

        var id = CreatePromiseList(db, count, out var members);
        GC.Collect();

        // Now check that the promise is complete on retrieving it
        // again, from its serialized form.
        var promise = db.GetPromiseById(id);
        Assert.NotNull(promise);
        var promiseList = promise!.ResultOutput as PromiseList;
        Assert.NotNull(promiseList);
        Assert.True(promiseList!.IsComplete);

        // Check members are the same
        for (int i = 0; i < count; ++i)
        {
            var t = promiseList.TryGetMemberPromiseAsync(i, default);
            var memberId = PromiseSerializationTests.GetSynchronousResult(t)!.Id;
            Assert.Equal(members[i], memberId);
        }
    }
}
