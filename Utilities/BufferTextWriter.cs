// Based on code from Nerdbank.Streams:
// https://github.com/AArnott/Nerdbank.Streams/
//
// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Utilities;

/// <summary>
/// Writes encoded text into an instance of <see cref="IBufferWriter{T}" />.
/// </summary>
/// <remarks>
/// This class uses memory more parsimoniously than layering <see cref="StreamWriter" />
/// on top of <see cref="MemoryStream" />.
/// </remarks>
public sealed class BufferTextWriter : TextWriter
{
    /// <summary>
    /// A buffer of written characters that have not yet been encoded.
    /// The <see cref="_charBufferPosition"/> field tracks how many characters are represented in this buffer.
    /// </summary>
    private readonly char[] _charBuffer = new char[512];

    /// <summary>
    /// The internal buffer writer to use for writing encoded characters.
    /// </summary>
    private readonly IBufferWriter<byte> _bufferWriter;

    /// <summary>
    /// The last buffer received from <see cref="_bufferWriter"/>.
    /// </summary>
    private Memory<byte> _memory;

    /// <summary>
    /// The number of characters written to the <see cref="_memory"/> buffer.
    /// </summary>
    private int _memoryPosition;

    /// <summary>
    /// The number of characters written to the <see cref="_charBuffer"/>.
    /// </summary>
    private int _charBufferPosition;

    /// <summary>
    /// Whether the encoding preamble has been written.
    /// </summary>
    private bool _preambleWritten;

    /// <summary>
    /// The encoding currently in use.
    /// </summary>
    private readonly Encoding _encoding;

    /// <summary>
    /// The preamble for the current <see cref="_encoding"/>.
    /// </summary>
    /// <remarks>
    /// We store this as a field to avoid calling <see cref="Encoding.GetPreamble"/> repeatedly,
    /// since the typical implementation allocates a new array for each call.
    /// </remarks>
    private ReadOnlyMemory<byte> _encodingPreamble;

    /// <summary>
    /// An encoder obtained from the current <see cref="_encoding"/> used for incrementally encoding written characters.
    /// </summary>
    private readonly Encoder _encoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferTextWriter"/> class.
    /// </summary>
    /// <param name="bufferWriter">The buffer writer to write to.</param>
    /// <param name="encoding">The encoding to use.</param>
    public BufferTextWriter(IBufferWriter<byte> bufferWriter, Encoding encoding)
    {
        _preambleWritten = false;
        _bufferWriter = bufferWriter ?? throw new ArgumentNullException(nameof(bufferWriter));
        _encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        _encoder = _encoding.GetEncoder();
        _encodingPreamble = _encoding.GetPreamble();
    }

    /// <inheritdoc />
    public override Encoding Encoding => _encoding;

    /// <summary>
    /// Gets the number of uninitialized characters remaining in <see cref="_charBuffer"/>.
    /// </summary>
    private int CharBufferSlack => _charBuffer.Length - _charBufferPosition;

    /// <inheritdoc />
    public override void Flush()
    {
        EncodeCharacters(flushEncoder: true);
        CommitBytes();
    }

    /// <inheritdoc />
    public override Task FlushAsync()
    {
        try
        {
            Flush();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    /// <inheritdoc />
    public override void Write(char value)
    {
        _charBuffer[_charBufferPosition++] = value;
        EncodeCharactersIfBufferFull();
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (value == null)
            return;

        Write(value.AsSpan());
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count)
        => Write(buffer.AsSpan(index, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer)
    {
        // Try for fast path
        if (buffer.Length <= CharBufferSlack)
        {
            buffer.CopyTo(_charBuffer.AsSpan(_charBufferPosition));
            _charBufferPosition += buffer.Length;
            EncodeCharactersIfBufferFull();
        }
        else
        {
            int charsCopied = 0;
            while (charsCopied < buffer.Length)
            {
                int charsToCopy = Math.Min(buffer.Length - charsCopied, CharBufferSlack);
                buffer.Slice(charsCopied, charsToCopy).CopyTo(_charBuffer.AsSpan(_charBufferPosition));
                charsCopied += charsToCopy;
                _charBufferPosition += charsToCopy;
                EncodeCharactersIfBufferFull();
            }
        }
    }

    /// <inheritdoc />
    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        Write(buffer);
        WriteLine();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Flush();

        base.Dispose(disposing);
    }

    /// <summary>
    /// Encodes the written characters if the character buffer is full.
    /// </summary>
    private void EncodeCharactersIfBufferFull()
    {
        if (_charBufferPosition == _charBuffer.Length)
            EncodeCharacters(flushEncoder: false);
    }

    /// <summary>
    /// Encodes characters written so far to a buffer provided by the underyling <see cref="_bufferWriter"/>.
    /// </summary>
    /// <param name="flushEncoder"><c>true</c> to flush the characters in the encoder; useful when finalizing the output.</param>
    private void EncodeCharacters(bool flushEncoder)
    {
        if (_charBufferPosition > 0)
        {
            int maxBytesLength = Encoding.GetMaxByteCount(_charBufferPosition);
            if (!_preambleWritten)
                maxBytesLength += _encodingPreamble.Length;

            if (_memory.Length - _memoryPosition < maxBytesLength)
            {
                CommitBytes();
                _memory = _bufferWriter.GetMemory(maxBytesLength);
            }

            if (!_preambleWritten)
            {
                _encodingPreamble.Span.CopyTo(_memory.Span.Slice(_memoryPosition));
                _memoryPosition += _encodingPreamble.Length;
                _preambleWritten = true;
            }

            if (MemoryMarshal.TryGetArray(_memory, out ArraySegment<byte> segment))
            {
                _memoryPosition += _encoder.GetBytes(_charBuffer, 0, _charBufferPosition, segment.Array!, segment.Offset + _memoryPosition, flush: flushEncoder);
            }
            else
            {
                byte[] rentedByteBuffer = ArrayPool<byte>.Shared.Rent(maxBytesLength);
                try
                {
                    int bytesWritten = _encoder.GetBytes(_charBuffer, 0, _charBufferPosition, rentedByteBuffer, 0, flush: flushEncoder);
                    rentedByteBuffer.CopyTo(_memory.Span.Slice(_memoryPosition));
                    _memoryPosition += bytesWritten;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rentedByteBuffer);
                }
            }

            _charBufferPosition = 0;

            if (_memoryPosition == _memory.Length)
                Flush();
        }
    }

    /// <summary>
    /// Commits any written bytes to the underlying <see cref="_bufferWriter"/>.
    /// </summary>
    private void CommitBytes()
    {
        if (_memoryPosition > 0)
        {
            _bufferWriter.Advance(_memoryPosition);
            _memoryPosition = 0;
            _memory = default;
        }
    }
}
