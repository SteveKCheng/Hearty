using System;
using System.Buffers;

namespace Hearty.Utilities;

/// <summary>
/// Manages a linked list of managed arrays to sequentially store data of 
/// variable length.
/// </summary>
/// <typeparam name="T">The type of each data element to store. </typeparam>
/// <remarks>
/// <para>
/// This buffer writer can be used to efficiently receive bulk data (over a network)
/// where an array of the exact size cannot be allocated upfront.
/// </para>
/// <para>
/// Unlike <see cref="ArrayBufferWriter{T}"/>, this buffer writer does not 
/// copy data from an old buffer to a new buffer when the buffer needs
/// to be enlarged.  The already written buffers are kept and returned
/// together as a <see cref="ReadOnlySequence{T}"/> at the end.  
/// </para>
/// <para>
/// As the arrays are managed entirely by garbage collection, this buffer
/// writer can be a structure instead of a class, thus saving one GC
/// allocation per use.
/// </para>
/// </remarks>
public class SegmentedArrayBufferWriter<T> : IBufferWriter<T>
{
    /// <summary>
    /// The size assumed when allocating buffers initially, unless
    /// a larger size is specifically requested.
    /// </summary>
    public int InitialBufferSize { get; }

    /// <summary>
    /// How many times buffers of defaultly sized at <see cref="CurrentBufferSize"/>
    /// can get allocated before the default allocation size doubles.
    /// </summary>
    /// <remarks>
    /// If zero, the default buffer size is always the same. 
    /// </remarks>
    public int DoublingThreshold { get; }

    /// <summary>
    /// The size of upcoming allocations of new buffers, unless their
    /// sizes are overridden to be bigger.
    /// </summary>
    public int CurrentBufferSize { get; private set; }

    /// <summary>
    /// Number of allocation requests for new buffers so far.
    /// </summary>
    public int NumberOfAllocations { get; private set; }

    /// <summary>
    /// Prepare to write to a growing sequence of buffers.
    /// </summary>
    /// <param name="initialBufferSize">
    /// The initial allocation size of buffers.
    /// </param>
    /// <param name="doublingThreshold">
    /// How many times buffers of the current size
    /// can get allocated before doubling the allocation size.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="initialBufferSize" /> is zero or negative, or too large;
    /// or <paramref name="doublingThreshold" /> is too large. 
    /// </exception>
    public SegmentedArrayBufferWriter(int initialBufferSize,
                                      int doublingThreshold)
    {
        if (initialBufferSize < 0 || initialBufferSize > 16 * 1048576)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBufferSize),
                "Initial size for the buffers must be between 1 to 10^24. ");
        }

        if (doublingThreshold < 0 || doublingThreshold > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(doublingThreshold),
                "Threshold for doubling the segment size must be between 1 and 64, or zero. ");
        }

        InitialBufferSize = initialBufferSize;
        DoublingThreshold = doublingThreshold;
        CurrentBufferSize = initialBufferSize;

        NumberOfAllocations = 0;

        _countdownToGrowSegments = 0;
        _startSegment = _endSegment = null;
        _lastBuffer = default;
        _lastBufferRemaining = 0;
    }

    /// <summary>
    /// The first segment in the linked list of buffers committed so far, if non-empty.
    /// </summary>
    private MemorySegment<T>? _startSegment;

    /// <summary>
    /// The last segment in the linked list of buffers committed so far, if non-empty.
    /// </summary>
    private MemorySegment<T>? _endSegment;

    /// <summary>
    /// Counts from <see cref="DoublingThreshold"/> to zero each time a buffer without overridden
    /// size is allocated, to know when to double <see cref="CurrentBufferSize"/>.
    /// </summary>
    private int _countdownToGrowSegments;

    /// <summary>
    /// The number of elements remaining that can be written into <see cref="_lastBuffer" />.
    /// </summary>
    private int _lastBufferRemaining;

    /// <summary>
    /// The buffer that allocation requests are satisfied from if there is sufficient
    /// space, that has yet to be committed into the linked list of buffers.
    /// </summary>
    private Memory<T> _lastBuffer;

    /// <inheritdoc />
    public void Advance(int count)
    {
        if (count < 0 || count > _lastBufferRemaining) 
            throw new ArgumentOutOfRangeException(nameof(count));

        _lastBufferRemaining -= count;
    }

    /// <summary>
    /// Allocate a new buffer and set it as the last buffer, replacing the previous buffer.
    /// </summary>
    /// <param name="sizeHint">
    /// The minimum required size of the new buffer.
    /// The new buffer will be sized to the maximum of this size or <see cref="CurrentBufferSize"/>,
    /// or a doubling of <see cref="CurrentBufferSize"/> if the threshold has been reached.
    /// </param>
    /// <returns>
    /// The new buffer, which is also set into <see cref="_lastBuffer"/>.
    /// </returns>
    private Memory<T> MakeNewBuffer(int sizeHint)
    {
        int size = CurrentBufferSize;

        if (sizeHint > size)
        {
            size = sizeHint;
        }
        else if (--_countdownToGrowSegments <= 0)
        {
            // Double the buffer size once we hit the threshold, unless
            // doubling overflows System.Int32 (so newSize <= 0) or
            // or this is the very first allocation, or doubling has
            // been disabled (so countdown < 0).
            var newSize = unchecked((int)((uint)size * 2));
            if (newSize > 0 && _countdownToGrowSegments == 0)
                CurrentBufferSize = size = newSize;

            _countdownToGrowSegments = DoublingThreshold;
        }

        var buffer = new T[size];

        _lastBuffer = buffer;
        _lastBufferRemaining = size;

        NumberOfAllocations++;

        return buffer;
    }

    /// <summary>
    /// Commit the contents written so far into the linked list of segments
    /// to eventually return to the client.
    /// </summary>
    /// <remarks>
    /// The last buffer remains active in case it can be used to satisfy
    /// a next call to <see cref="GetMemory" />.
    /// </remarks>
    private void CommitLastBuffer()
    {
        var buffer = _lastBuffer;
        var used = buffer.Length - _lastBufferRemaining;

        if (used == 0)
            return;

        _lastBuffer = buffer.Slice(used, _lastBufferRemaining);
        buffer = buffer.Slice(0, used);

        if (_endSegment != null)
            _endSegment = _endSegment.Append(buffer);
        else
            _startSegment = _endSegment = new MemorySegment<T>(buffer);
    }

    /// <inheritdoc />
    public Memory<T> GetMemory(int sizeHint = 0)
    {
        // Use the remaining space in the last buffer if enough
        int remaining = _lastBufferRemaining;
        if (remaining > sizeHint)
            return _lastBuffer.Slice(_lastBuffer.Length - remaining, remaining);

        // Commit the existing buffer and then append a new segment
        CommitLastBuffer();
        return MakeNewBuffer(sizeHint);
    }

    /// <inheritdoc />
    public Span<T> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    /// <summary>
    /// Get the sequence of data that has been written by the client and committed
    /// (via <see cref="Advance"/>).
    /// </summary>
    public ReadOnlySequence<T> GetWrittenSequence()
    {
        CommitLastBuffer();

        if (_startSegment == null)
            return new ReadOnlySequence<T>(new ReadOnlyMemory<T>());

        if (_startSegment.Next == null)
            return new ReadOnlySequence<T>(_startSegment.Memory);
        else
            return new ReadOnlySequence<T>(_startSegment, 0, _endSegment!, _endSegment!.Memory.Length);
    }
}

/// <summary>
/// Basic implementation of <see cref="ReadOnlySequenceSegment{T}"/> where no explicit
/// memory management is needed for the segments.
/// </summary>
/// <typeparam name="T">The type of each data element. </typeparam>
internal sealed class MemorySegment<T> : ReadOnlySequenceSegment<T>
{
    /// <summary>
    /// Construct a single segment without any segment following afterwards.
    /// </summary>
    /// <param name="memory">The memory buffer for the new segment. </param>
    public MemorySegment(in ReadOnlyMemory<T> memory) => Memory = memory;

    /// <summary>
    /// Chain in a new segment after the current segment, in the linked list of segments.
    /// </summary>
    /// <param name="memory">The memory buffer for the new segment. </param>
    /// <returns>The new segment at the end of the linked list. </returns>
    public MemorySegment<T> Append(in ReadOnlyMemory<T> memory)
    {
        var segment = new MemorySegment<T>(memory);
        segment.RunningIndex = this.RunningIndex + this.Memory.Length;
        this.Next = segment;
        return segment;
    }
}
