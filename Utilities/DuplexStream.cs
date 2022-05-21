using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Utilities;

/// <summary>
/// Joins one reading stream and one writing stream to form
/// a full-duplex stream.
/// </summary>
public sealed class DuplexStream : Stream
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;

    /// <summary>
    /// Pairs one stream for reading with one stream for writing.
    /// </summary>
    /// <param name="readStream">The stream to read data from. </param>
    /// <param name="writeStream">The stream to write data to. </param>
    public DuplexStream(Stream readStream, Stream writeStream)
    {
        _readStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        _writeStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
    }

    /// <summary>
    /// Create two streams that bi-directionally communicate with each
    /// other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These streams behave as if they are sockets connected to one another.
    /// Usually they are used for testing .NET code to communicate over
    /// a network without the code on two separate hosts or processes.
    /// </para>
    /// <para>
    /// These streams are implemented entirely in managed code without
    /// using (kernel-level) operating system facilities like named pipes.
    /// Thus they are more efficient for .NET code but can only communicate
    /// with .NET code in the current process.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Two streams whose read and write sides are criss-crossed.
    /// </returns>
    public static (DuplexStream, DuplexStream) CreatePair()
    {
        var pipe1 = new Pipe();
        var pipe2 = new Pipe();

        var stream1 = new DuplexStream(pipe1.Reader.AsStream(), pipe2.Writer.AsStream());
        var stream2 = new DuplexStream(pipe2.Reader.AsStream(), pipe1.Writer.AsStream());
        return (stream1, stream2);
    }

    /// <inheritdoc />
    public override bool CanRead => _readStream.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _writeStream.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override bool CanTimeout => _readStream.CanTimeout || _writeStream.CanTimeout;

    /// <inheritdoc />
    public override int ReadTimeout 
    {
        get => _readStream.ReadTimeout;
        set => _readStream.ReadTimeout = value; 
    }

    /// <inheritdoc />
    public override int WriteTimeout 
    {
        get => _writeStream.WriteTimeout;
        set => _writeStream.WriteTimeout = value;
    }

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _writeStream.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
        => _writeStream.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => _readStream.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _readStream.Read(buffer);

    /// <inheritdoc />
    public override int ReadByte() => _readStream.ReadByte();

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _readStream.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _readStream.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
        => _writeStream.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => _writeStream.Write(buffer);

    /// <inheritdoc />
    public override void WriteByte(byte value) => _writeStream.WriteByte(value);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _writeStream.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _writeStream.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void CopyTo(Stream destination, int bufferSize)
        => _readStream.CopyTo(destination, bufferSize);

    /// <inheritdoc />
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        => _readStream.CopyToAsync(destination, bufferSize, cancellationToken);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _readStream.Dispose();
            _writeStream.Dispose();
        }

        base.Dispose(disposing);
    }
}
