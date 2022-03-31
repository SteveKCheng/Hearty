using System;
using System.Buffers;
using System.Linq;
using System.Text;
using Xunit;
using Hearty.Server;
using System.Threading.Tasks;

namespace Hearty.Tests;

public class PromiseSerializationTests
{
    private class PromiseDataFixtures : IPromiseDataFixtures
    {
        public PromiseStorage PromiseStorage { get; } = new BasicPromiseStorage();

        public PromiseDataSchemas Schemas { get; } = new PromiseDataSchemas();
    }

    private readonly PromiseDataFixtures _fixtures = new();

    private static Payload CreateTestPayload()
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

        return new Payload("text/plain; charset=utf-8", bytes);
    }

    [Fact]
    public void SerializePayload()
    {
        var payload = CreateTestPayload();
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

    private static Exception CreateTestException()
    {
        return new ObjectDisposedException(
            "This is a dummy exception to test serialization. ");
    }

    [Fact]
    public void SerializeException()
    {
        var data = new PromiseExceptionalData(CreateTestException());
        Assert.True(data.TryPrepareSerialization(out var info));

        var buffer = new byte[info.PayloadLength];
        info.Serializer!.Invoke(info, buffer);

        var data2 = PromiseExceptionalData.Deserialize(_fixtures, buffer);

        Assert.Equal(data.IsCancellation, data2.IsCancellation);
        Assert.Equal(data.IsTransient, data2.IsTransient);
        Assert.Equal(data.IsComplete, data2.IsComplete);

        static ReadOnlySequence<byte> GetMessagePackPayload(PromiseData data)
        {
            for (int i = 0; i < data.CountFormats; ++i)
            {
                if (data.GetFormatInfo(i).MediaType.IsSubsetOf(ServedMediaTypes.MsgPack))
                    return GetSynchronousResult(data.GetPayloadAsync(i, default));
            }

            throw new InvalidOperationException("Promise data does not make available its payload in MessagePack format. ");
        }

        var payload1 = GetMessagePackPayload(data);
        var payload2 = GetMessagePackPayload(data2);

        Assert.True(SequenceEquals(payload1, payload2));
    }

    private static T GetSynchronousResult<T>(in ValueTask<T> task)
    {
        Assert.True(task.IsCompleted);
        return task.Result;
    }

    [Fact]
    public void SerializePromiseList()
    {
        var payloadPromise = _fixtures.PromiseStorage.CreatePromise(
                            input: null,
                            output: CreateTestPayload());

        var promiseList = new PromiseList(_fixtures.PromiseStorage);
        IPromiseListBuilder builder = promiseList;
        builder.SetMember(0, payloadPromise);
        builder.TryComplete(1, CreateTestException());

        Assert.True(promiseList.TryPrepareSerialization(out var info));
        var buffer = new byte[info.PayloadLength];
        info.Serializer!.Invoke(info, buffer);

        var promiseList2 = PromiseList.Deserialize(_fixtures, buffer);

        Assert.True(promiseList2.IsComplete);

        var payloadPromise1 = GetSynchronousResult(promiseList2.TryGetMemberPromiseAsync(0));

        // Should be the same object actually
        Assert.Equal(payloadPromise, payloadPromise1);

        var exceptionData = promiseList2.ExceptionData;
        Assert.NotNull(exceptionData);
        Assert.True(exceptionData!.IsFailure);
    }
}
