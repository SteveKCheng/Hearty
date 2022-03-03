using Hearty.Utilities;
using MessagePack;
using System;
using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Hearty.Server.Program
{
    public static class JsonPayloadTranscoding
    {
        public static ImmutableArray<TranscodedFormatInfo<ReadOnlySequence<byte>>> Spec { get; } = CreateSpec();

        private static ImmutableArray<TranscodedFormatInfo<ReadOnlySequence<byte>>> CreateSpec()
        {
            var builder = ImmutableArray.CreateBuilder<TranscodedFormatInfo<ReadOnlySequence<byte>>>();
            builder.Add(new(new ContentFormatInfo(ServedMediaTypes.Json, ContentPreference.Best),
                            input => input));
            builder.Add(new(new ContentFormatInfo(ServedMediaTypes.MsgPack, ContentPreference.Best),
                            ConvertJsonToMessagePack));
            return builder.ToImmutableArray();
        }

        private static ReadOnlySequence<byte> ConvertJsonToMessagePack(ReadOnlySequence<byte> json)
        {
            // Not efficient but will do for a demonstration
            using var streamReader = new StreamReader(new MemoryReadingStream(json), Encoding.UTF8);
            var bufferWriter = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(bufferWriter);
            MessagePackSerializer.ConvertFromJson(streamReader, ref writer);
            writer.Flush();
            return new ReadOnlySequence<byte>(bufferWriter.WrittenMemory);
        }

        public static Func<object, SimplePayload, ValueTask<PromiseData>> JobOutputDeserializer { get; }
            = SerializeJobOutput;

        private static ValueTask<PromiseData> SerializeJobOutput(object _, SimplePayload p)
        {
            PromiseData d = new TranscodingPayload<ReadOnlySequence<byte>>(p.Body, Spec);
            return ValueTask.FromResult(d);
        }
    }
}
