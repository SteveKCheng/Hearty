using Hearty.Server;
using System;
using System.Buffers;
using System.Linq;
using System.Text;
using Xunit;

namespace Hearty.Tests;

public class PromiseSerializationTests
{
    private class PromiseDataFixtures : IPromiseDataFixtures
    {
        public PromiseStorage PromiseStorage { get; } = new BasicPromiseStorage();

        public PromiseDataSchemas Schemas { get; } = new PromiseDataSchemas();
    }

    private readonly PromiseDataFixtures _fixtures = new();

    [Fact]
    public void SerializePayload()
    {
        var bytes = new UTF8Encoding(false).GetBytes(
@"Shall I compare thee to a summer’s day?
Thou art more lovely and more temperate:
Rough winds do shake the darling buds of May,
And summer’s lease hath all too short a date;
Sometime too hot the eye of heaven shines,
And often is his gold complexion dimm’d;
And every fair from fair sometime declines,
By chance or nature’s changing course untrimm’d;
But thy eternal summer shall not fade,
Nor lose possession of that fair thou ow’st;
Nor shall death brag thou wander’st in his shade,
When in eternal lines to time thou grow’st:
So long as men can breathe or eyes can see,
So long lives this, and this gives life to thee. ");

        var payload = new Payload("text/plain; charset=utf-8", bytes);

        Assert.True(payload.TryPrepareSerialization(out var info));

        var buffer = new byte[info.PayloadLength];
        info.Serializer!.Invoke(info, buffer);

        // De-serialize it back
        var payload2 = Payload.Deserialize(_fixtures, buffer);

        // Compare
        Assert.Equal(payload.IsFailure, payload2.IsFailure);
        Assert.Equal(payload.IsTransient, payload2.IsTransient);
        Assert.Equal(payload.IsComplete, payload2.IsComplete);
        Assert.Equal(payload.ContentType, payload2.ContentType);

        Assert.True(SequenceEquals(payload.Body, payload2.Body));
    }

    private static bool SequenceEquals(ReadOnlySequence<byte> a, ReadOnlySequence<byte> b)
    {
        var readerA = new SequenceReader<byte>(a);
        var readerB = new SequenceReader<byte>(b);

        while (true)
        {
            var spanA = readerA.UnreadSpan;
            var spanB = readerB.UnreadSpan;
            int len = Math.Min(spanA.Length, spanB.Length);
            if (len == 0)
                return true;

            if (!spanA[0..len].SequenceEqual(spanB[0..len]))
                return false;

            readerA.Advance(len);
            readerB.Advance(len);
        }
    }
}
