using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hearty.Client
{
    internal ref partial struct ValueStringBuilder
    {
        private Span<char> _chars;
        private int _pos;

        public ValueStringBuilder(Span<char> initialBuffer)
        {
            _chars = initialBuffer;
            _pos = 0;
        }

        public int Length
        {
            get => _pos;
            set
            {
                Debug.Assert(value >= 0);
                Debug.Assert(value <= _chars.Length);
                _pos = value;
            }
        }

        public int Capacity => _chars.Length;

        public void EnsureCapacity(int capacity)
        {
            // This is not expected to be called this with negative capacity
            Debug.Assert(capacity >= 0);

            // If the caller has a bug and calls this with negative capacity, make sure to call Grow to throw an exception.
            if ((uint)capacity > (uint)_chars.Length)
                Grow(capacity - _pos);
        }

        /// <summary>
        /// Get a pinnable reference to the builder.
        /// </summary>
        public ref char GetPinnableReference() => ref MemoryMarshal.GetReference(_chars);

        public ref char this[int index]
        {
            get
            {
                Debug.Assert(index < _pos);
                return ref _chars[index];
            }
        }

        public override string ToString() => _chars.Slice(0, _pos).ToString();

        /// <summary>Returns the underlying storage of the builder.</summary>
        public Span<char> RawChars => _chars;

        /// <summary>
        /// Returns a span around the contents of the builder.
        /// </summary>
        public ReadOnlySpan<char> AsSpan() => _chars.Slice(0, _pos);

        public bool TryCopyTo(Span<char> destination, out int charsWritten)
        {
            if (_chars.Slice(0, _pos).TryCopyTo(destination))
            {
                charsWritten = _pos;
                return true;
            }
            else
            {
                charsWritten = 0;
                return false;
            }
        }

        public void Insert(int index, char value, int count)
        {
            if (_pos > _chars.Length - count)
                Grow(count);

            int remaining = _pos - index;
            _chars.Slice(index, remaining).CopyTo(_chars.Slice(index + count));
            _chars.Slice(index, count).Fill(value);
            _pos += count;
        }

        public void Insert(int index, string? s)
        {
            if (s == null)
                return;

            int count = s.Length;

            if (_pos > (_chars.Length - count))
                Grow(count);

            int remaining = _pos - index;
            _chars.Slice(index, remaining).CopyTo(_chars.Slice(index + count));
            s.CopyTo(_chars.Slice(index));
            _pos += count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(char c)
        {
            int pos = _pos;
            if ((uint)pos < (uint)_chars.Length)
            {
                _chars[pos] = c;
                _pos = pos + 1;
            }
            else
            {
                GrowAndAppend(c);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(string? s)
        {
            if (s == null)
                return;

            int pos = _pos;
            if (s.Length == 1 && (uint)pos < (uint)_chars.Length) // very common case, e.g. appending strings from NumberFormatInfo like separators, percent symbols, etc.
            {
                _chars[pos] = s[0];
                _pos = pos + 1;
            }
            else
            {
                AppendSlow(s);
            }
        }

        private void AppendSlow(string s)
        {
            int pos = _pos;
            if (pos > _chars.Length - s.Length)
                Grow(s.Length);

            s.CopyTo(_chars.Slice(pos));
            _pos += s.Length;
        }

        public void Append(char c, int count)
        {
            if (_pos > _chars.Length - count)
                Grow(count);

            Span<char> dst = _chars.Slice(_pos, count);
            for (int i = 0; i < dst.Length; i++)
                dst[i] = c;

            _pos += count;
        }

        public void Append(ReadOnlySpan<char> value)
        {
            int pos = _pos;
            if (pos > _chars.Length - value.Length)
                Grow(value.Length);

            value.CopyTo(_chars.Slice(_pos));
            _pos += value.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<char> AppendSpan(int length)
        {
            int origPos = _pos;
            if (origPos > _chars.Length - length)
                Grow(length);

            _pos = origPos + length;
            return _chars.Slice(origPos, length);
        }

        public void Append<T>(T value) where T : ISpanFormattable
            => Append<T>(value, string.Empty);

        public void Append<T>(T value, ReadOnlySpan<char> format) where T : ISpanFormattable
        {
            int pos = _pos;
            int charsWritten;
            while (!value.TryFormat(_chars.Slice(pos), out charsWritten, format, null))
                Grow(Math.Min(4, _chars.Length - pos) * 2);

            _pos += charsWritten;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowAndAppend(char c)
        {
            Grow(1);
            Append(c);
        }

        /// <summary>
        /// Resize the internal buffer either by doubling current buffer size or
        /// by adding <paramref name="additionalCapacityBeyondPos"/> to
        /// <see cref="_pos"/> whichever is greater.
        /// </summary>
        /// <param name="additionalCapacityBeyondPos">
        /// Number of chars requested beyond current position.
        /// </param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int additionalCapacityBeyondPos)
        {
            Debug.Assert(additionalCapacityBeyondPos > 0);
            Debug.Assert(_pos > _chars.Length - additionalCapacityBeyondPos, "Grow called incorrectly, no resize is needed.");

            char[] array = new char[(int)Math.Max((uint)(_pos + additionalCapacityBeyondPos), (uint)_chars.Length * 2)];

            _chars.Slice(0, _pos).CopyTo(array);

            _chars = array;
        }
    }
}
