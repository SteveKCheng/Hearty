using Hearty.Utilities;
using System;
using System.Buffers;
using System.Linq;
using Xunit;

namespace Hearty.Tests;
public class StreamTests
{
    [Fact]
    public void ReadFromMemoryReadingStream()
    {
        // Generate random bytes
        var random = new Random(Seed: 47);
        var bufferWriter = new SegmentedArrayBufferWriter<byte>(
                            initialBufferSize: 1024, 
                            doublingThreshold: 1);
        for (int i = 0; i < 5; ++i)
        {
            var span = bufferWriter.GetSpan(1024);
            random.NextBytes(span);
            bufferWriter.Advance(1024);
        }

        var source = bufferWriter.GetWrittenSequence();

        var stream = new MemoryReadingStream(source);
        var buffer = new byte[source.Length + 400];

        for (int i = 0; i < 2; ++i)
        {
            int startOffset = (i == 0) ? 200 : 0;
            stream.Position = startOffset;

            int offset = 0;
            int bytesRead;
            do
            {
                bytesRead = stream.Read(buffer, offset, 400);
                offset += bytesRead;
            } while (bytesRead > 0);

            Assert.Equal(source.Length - startOffset, offset);
            Assert.Equal(source.Length, stream.Position);
        }

        Assert.True(SequenceEquals(
            source, 
            new ReadOnlySequence<byte>(buffer, 0, (int)source.Length)));
    }

    public static bool SequenceEquals(ReadOnlySequence<byte> a, ReadOnlySequence<byte> b)
    {
        if (a.Length != b.Length)
            return false;

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
