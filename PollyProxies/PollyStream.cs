using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Polly;

namespace Hearty.PollyProxies;

/// <summary>
/// Wraps an existing stream to make its I/O calls go through the
/// Polly resilience framework. 
/// </summary>
public sealed class PollyStream : Stream
{
    private readonly Stream _source;
    private readonly ISyncPolicy _syncPolicy;
    private readonly IAsyncPolicy _asyncPolicy;
    private readonly Context _pollyContext;

    /// <summary>
    /// Construct from an existing stream.
    /// </summary>
    /// <param name="source">
    /// The existing stream, which becomes owned by this new wrapper object.
    /// </param>
    /// <param name="syncPolicy">
    /// Execution policy under Polly for synchronous I/O methods.
    /// </param>
    /// <param name="asyncPolicy">
    /// Execution policy under Polly for asynchronous I/O methods.
    /// </param>
    /// <param name="pollyContext">
    /// Context object to pass to Polly.
    /// </param>
    public PollyStream(Stream source, 
                       ISyncPolicy syncPolicy, 
                       IAsyncPolicy asyncPolicy, 
                       Context pollyContext)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(syncPolicy);
        ArgumentNullException.ThrowIfNull(asyncPolicy);

        _source = source;
        _syncPolicy = syncPolicy;
        _asyncPolicy = asyncPolicy;
        _pollyContext = pollyContext;
    }

    /// <inheritdoc />
    public override bool CanRead => _source.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _source.CanWrite;

    /// <inheritdoc />
    public override bool CanWrite => _source.CanWrite;

    /// <inheritdoc />
    public override bool CanTimeout => _source.CanTimeout;

    /// <inheritdoc />
    public override int ReadTimeout => _source.ReadTimeout;

    /// <inheritdoc />
    public override int WriteTimeout => _source.WriteTimeout;

    /// <inheritdoc />
    public override long Length => _source.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    /// <inheritdoc />
    public override void Close() => _source.Close();

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => _source.DisposeAsync();

    /// <inheritdoc />
    public override void Flush() => _syncPolicy.Execute(_ => _source.Flush(), _pollyContext);

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
        => _asyncPolicy.ExecuteAsync(_ => _source.FlushAsync(cancellationToken), _pollyContext);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
        => _syncPolicy.Execute(_ => _source.Seek(offset, origin), _pollyContext);

    /// <inheritdoc />
    public override void SetLength(long value)
        => _syncPolicy.Execute(_ => _source.SetLength(value), _pollyContext);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => _syncPolicy.Execute(_ => _source.Read(buffer, offset, count), _pollyContext);

    /// <inheritdoc />
    public override int ReadByte()
        => _syncPolicy.Execute(_ => _source.ReadByte(), _pollyContext);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _asyncPolicy.ExecuteAsync(_ => _source.ReadAsync(buffer, offset, count, cancellationToken), _pollyContext);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => new ValueTask<int>(_asyncPolicy.ExecuteAsync(
            _ => _source.ReadAsync(buffer, cancellationToken).AsTask(), _pollyContext));

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
        => _syncPolicy.Execute(_ => _source.Write(buffer, offset, count), _pollyContext);

    /// <inheritdoc />
    public override void WriteByte(byte value)
        => _syncPolicy.Execute(_ => _source.WriteByte(value), _pollyContext);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _asyncPolicy.ExecuteAsync(_ => _source.WriteAsync(buffer, offset, count, cancellationToken), _pollyContext);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => new ValueTask(
            _asyncPolicy.ExecuteAsync(_ => _source.WriteAsync(buffer, cancellationToken).AsTask(), _pollyContext));

    /// <inheritdoc />
    public override unsafe int Read(Span<byte> buffer)
    {
        // Hack because ref structs cannot be closed over
        int length = buffer.Length;
        fixed (byte* p = buffer)
        {
            var p_ = (IntPtr)p; // avoid compiler error with passing in p
            return _syncPolicy.Execute(_ => _source.Read(new Span<byte>((byte*)p_, length)), _pollyContext);
        }
    }

    /// <inheritdoc />
    public override unsafe void Write(ReadOnlySpan<byte> buffer)
    {
        // Hack because ref structs cannot be closed over
        int length = buffer.Length;
        fixed (byte* p = buffer)
        {
            var p_ = (IntPtr)p; // avoid compiler error with passing in p
            _syncPolicy.Execute(_ => _source.Write(new ReadOnlySpan<byte>((byte*)p_, length)), _pollyContext);
        }
    }
}
