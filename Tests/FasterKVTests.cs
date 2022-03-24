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

        db.TryAdd(ref state, key, (ref (Random random, double value) s, in PromiseId k) =>
        {
            s.value = s.random.NextDouble();
            return s.value;
        });

        Assert.True(db.ContainsKey(key));
        Assert.True(db.TryGetValue(key, out var retrievedValue));
        Assert.Equal(state.value, retrievedValue);
        Assert.InRange(state.value, 0.0, 1.0);
    }
}
