using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Utilities
{
    /// <summary>
    /// A stream that reads from a sequence of buffers in memory.
    /// </summary>
    public sealed class MemoryReadingStream : Stream
    {
        private readonly ReadOnlySequence<byte> _source;

        /// <summary>
        /// Wraps a read-only stream around a sequence of memory buffers.
        /// </summary>
        /// <param name="source">The source bytes 
        /// that the stream should provide. </param>
        public MemoryReadingStream(ReadOnlySequence<byte> source)
            => _source = source;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => true;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _source.Length;

        /// <inheritdoc />
        public override bool CanTimeout => false;

        private long _offset;
        private SequencePosition _position;

        /// <inheritdoc />
        public override long Position
        {
            get => _offset;
            set 
            {
                if (value < 0 || value > _source.Length)
                    throw new ArgumentOutOfRangeException(message: "Cannot seek to a position beyond the bounds of the in-memory data. ",
                                                          innerException: null);
                _position = _source.GetPosition(value);
                _offset = value;
            }
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
            => Read(new Span<byte>(buffer, offset, count));

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            long remaining = _source.Length - _offset;
            int toRead = remaining > buffer.Length ? buffer.Length 
                                                   : (int)remaining;
            var slice = _source.Slice(_position, toRead);
            slice.CopyTo(buffer);
            return toRead;
        }

        /// <inheritdoc />
        public override int ReadByte()
        {
            Span<byte> buffer = stackalloc byte[1];
            int r = Read(buffer);
            return r > 0 ? (int)buffer[0] : -1;
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
        {
            long baseOffset = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _offset,
                SeekOrigin.End => _source.Length,
                _ => throw new ArgumentException(message: "SeekOrigin parameter is invalid",
                                                 paramName: nameof(origin))
            };

            offset = checked(offset + baseOffset); 
            Position = offset;
            return offset;
        }

        /// <inheritdoc />
        public override void SetLength(long value) 
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Flush() { }

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override void WriteByte(byte value)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override void CopyTo(Stream destination, int bufferSize)
        {
            var slice = _source.Slice(_position);
 
            while (slice.Length > 0)
            {
                var segment = slice.FirstSpan;
                destination.Write(segment);
                slice = slice.Slice(segment.Length);
            }
        }
            
        /// <inheritdoc />
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            var slice = _source.Slice(_position);

            while (slice.Length > 0)
            {
                var segment = slice.First;
                await destination.WriteAsync(segment, cancellationToken)
                                 .ConfigureAwait(false);
                slice = slice.Slice(segment.Length);
            }
        }
    }
}
