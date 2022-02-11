using JobBank.Common;
using JobBank.Utilities;
using MessagePack;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Represents an exception (from job execution) as the
    /// output of a promise.
    /// </summary>
    public sealed class PromiseExceptionalData : PromiseData
    {
        private readonly ExceptionPayload _payload;

        /// <summary>
        /// Encodes strings to UTF-8 without the so-called "byte order mark".
        /// </summary>
        private static readonly Encoding Utf8NoBOM = new UTF8Encoding(false);

        /// <summary>
        /// Wrap <see cref="ExceptionPayload" /> to be usable as
        /// promise output.
        /// </summary>
        public PromiseExceptionalData(ExceptionPayload payload)
        {
            _payload = payload;
        }

        /// <summary>
        /// Convert a .NET exception to a form usable as promise output.
        /// </summary>
        public PromiseExceptionalData(Exception exception)
            : this(ExceptionPayload.CreateFromException(exception))
        {
        }

        /// <summary>
        /// Always returns true on this class: it represents a failure.
        /// </summary>
        public override bool IsFailure => true;

        public override bool IsTransient => _payload.Cancelling;

        private enum Format
        {
            Json = 0,
            MessagePack = 1,
            Text = 2
        }

        private static byte[] FormatAsText(ExceptionPayload payload)
        {
            var builder = new StringBuilder();
            builder.Append("Class: ");
            builder.AppendLine(payload.Class);
            builder.Append("Description: ");
            builder.AppendLine(payload.Description);
            builder.AppendLine("Stack Trace: ");
            builder.AppendLine(payload.Trace);
            return Utf8NoBOM.GetBytes(builder.ToString());
        }

        /// <inheritdoc />
        public override ValueTask<Stream> GetByteStreamAsync(int format, CancellationToken cancellationToken)
        {
            var payload = GetPayload(format, cancellationToken);
            Stream stream = new MemoryReadingStream(payload);
            return ValueTask.FromResult(stream);
        }

        private ReadOnlySequence<byte> GetPayload(int format, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = (Format)format switch
            {
                Format.Text => FormatAsText(_payload),
                Format.MessagePack => MessagePackSerializer.Serialize(_payload),
                Format.Json => JsonSerializer.SerializeToUtf8Bytes(_payload),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };

            return new ReadOnlySequence<byte>(bytes);
        }

        /// <inheritdoc />
        public override ValueTask<ReadOnlySequence<byte>> GetPayloadAsync(int format, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetPayload(format, cancellationToken));

        /// <inheritdoc />
        public override ValueTask WriteToPipeAsync(int format, PipeWriter writer, long position, CancellationToken cancellationToken)
        {
            var payload = GetPayload(format, cancellationToken).Slice(position);
            return writer.WriteAsync(payload, cancellationToken);
        }

        /// <inheritdoc />
        public override ContentFormatInfo GetFormatInfo(int format)
            => (Format)format switch
            {
                Format.Text => new("text/plain", ContentPreference.Fair),
                Format.Json => new("application/json", ContentPreference.Good),
                Format.MessagePack => new("application/messagepack", ContentPreference.Best),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
    }
}
