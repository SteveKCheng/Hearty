using System;
using System.Collections.Generic;
using Xunit;
using Hearty.Server.FasterKV;
using System.IO;
using System.Linq;

namespace Hearty.Tests;

public class FasterKVTests
{
    [Fact]
    public void Basic1()
    {
        uint serviceId = 0;
        uint seq = 1;
        int count = 0;

        var random = new Random(Seed: 89);

        using var db = new FasterDbDictionary<PromiseId, double>(new FasterDbFileOptions
        {
            Path = String.Empty,
            Preallocate = false,
            DeleteOnDispose = true
        });

        var key = new PromiseId(serviceId, seq++);
        var value = random.NextDouble();
        ++count;
        db.Add(key, value);

        Assert.True(db.ContainsKey(key));
        Assert.True(db.TryGetValue(key, out var retrievedValue));
        Assert.Equal(value, retrievedValue);

        var itemsList = db.ToList();
        Assert.Equal(count, itemsList.Count);
        Assert.Equal(KeyValuePair.Create(key, value), itemsList[0]);
    }

    [Fact]
    public void AddWithFactory()
    {
        uint serviceId = 0;
        uint seq = 1;

        using var db = new FasterDbDictionary<PromiseId, double>(new FasterDbFileOptions
        {
            Path = String.Empty,
            Preallocate = false,
            DeleteOnDispose = true
        });

        var key = new PromiseId(serviceId, seq++);

        var state = (random: new Random(Seed: 89), value: double.NaN);

        db.TryAdd(key, ref state, (ref (Random random, double value) s, in PromiseId k) =>
        {
            s.value = s.random.NextDouble();
            return s.value;
        }, out var storedValue);

        Assert.True(db.ContainsKey(key));
        Assert.True(db.TryGetValue(key, out var retrievedValue));
        Assert.Equal(state.value, retrievedValue);
        Assert.Equal(storedValue, retrievedValue);
        Assert.InRange(state.value, 0.0, 1.0);
    }

    [Fact]
    public void AddMany()
    {
        uint serviceId = 0;

        int count = 1 << 16;

        using var db = new FasterDbDictionary<PromiseId, double>(new FasterDbFileOptions
        {
            Path = String.Empty,
            Preallocate = false,
            DeleteOnDispose = true,
            HashIndexSize = count,

            PageLog2Size = 16,

            // There are 2^16 items to write but only 2^20 bytes of memory,
            // while each item takes up more than 16 bytes.  Both key and value
            // are 8 bytes but there is also the record header.
            //
            // This setting thus forces FASTER KV to swap to file-backed storage.
            // So, operations in FASTER KV can become asynchronous (but
            // our wrapper waits for them synchronously).
            MemoryLog2Capacity = 20
        }, new FasterDbPromiseComparer());

        for (int i = 0; i < count; ++i)
        {
            var key = new PromiseId(serviceId, (uint)(i + 1));
            var value = (double)(i + 1);
            db.Add(key, value);
        }

        // Re-read numbers some of which should come from file-backed storage
        for (int i = 0; i < count; ++i)
        {
            var key = new PromiseId(serviceId, (uint)(i + 1));
            Assert.True(db.TryGetValue(key, out var value));
            Assert.Equal((double)(i + 1), value);
        }
    }
}
