﻿using JobBank.Common;
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

        /// <inheritdoc />
        public override string SuggestedContentType => "application/json";

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

        private enum Format
        {
            Text,
            Json,
            MessagePack
        }

        private static Format GetFormat(string contentType)
        {
            if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
                return Format.Json;
            else if (string.Equals(contentType, "application/messagepack", StringComparison.OrdinalIgnoreCase))
                return Format.MessagePack;
            else
                return Format.Text;
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
        public override ValueTask<Stream> GetByteStreamAsync(string contentType, CancellationToken cancellationToken)
        {
            var payload = GetPayload(contentType, cancellationToken);
            return ValueTask.FromResult(CreatePipeReader(payload, 0).AsStream());
        }

        private ReadOnlySequence<byte> GetPayload(string contentType, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = GetFormat(contentType) switch
            {
                Format.Text => FormatAsText(_payload),
                Format.MessagePack => MessagePackSerializer.Serialize(_payload),
                Format.Json => JsonSerializer.SerializeToUtf8Bytes(_payload),
                _ => throw new NotImplementedException()
            };

            return new ReadOnlySequence<byte>(bytes);
        }

        /// <inheritdoc />
        public override ValueTask<ReadOnlySequence<byte>> GetPayloadAsync(string contentType, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetPayload(contentType, cancellationToken));

        private static PipeReader CreatePipeReader(ReadOnlySequence<byte> body, long position)
        {
            var pipeReader = PipeReader.Create(body);
            if (position > 0)
                pipeReader.AdvanceTo(body.GetPosition(position));
            return pipeReader;
        }

        /// <inheritdoc />
        public override ValueTask<IAsyncEnumerator<ReadOnlyMemory<byte>>> GetPayloadStreamAsync(string contentType, int position, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override ValueTask<PipeReader> GetPipeReaderAsync(string contentType, long position, CancellationToken cancellationToken)
        {
            var payload = GetPayload(contentType, cancellationToken);
            return ValueTask.FromResult(CreatePipeReader(payload, position));
        }
    }
}
