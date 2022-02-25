using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Hearty.Server.Tests;

internal class StreamWriterContent : HttpContent
{
    private readonly Func<Stream, Task> _writer;

    public StreamWriterContent(Func<Stream, Task> writer) => _writer = writer;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => _writer.Invoke(stream);

    protected override bool TryComputeLength(out long length)
    {
        length = default;
        return false;
    }
}
