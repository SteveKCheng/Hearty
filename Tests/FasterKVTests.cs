using System;
using System.Collections.Generic;
using Xunit;
using Hearty.Server.FasterKV;
using System.IO;

namespace Hearty.Tests;

public class FasterKVTests
{
    [Fact]
    public void Basic1()
    {
        uint serviceId = 0;
        uint seq = 1;

        var random = new Random(Seed: 89);

        using var db = new FasterDbDictionary<PromiseId, double>(new FasterDbFileOptions
        {
            Path = String.Empty,
            Preallocate = false,
            DeleteOnDispose = true
        });

        var key = new PromiseId(serviceId, seq++);
        var value = random.NextDouble();
        db.Add(key, value);

        Assert.True(db.ContainsKey(key));
        Assert.True(db.TryGetValue(key, out var retrievedValue));
        Assert.Equal(value, retrievedValue);
    }
}
