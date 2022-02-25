﻿using System;
using Hearty.Common;

namespace Hearty.Work
{
    /// <summary>
    /// An exception from a remote procedure call to a worker host.
    /// </summary>
    /// <remarks>
    /// The properties of this instance hold the data
    /// generated by .NET code in this process as this
    /// exception is thrown.  The exception data from the remote
    /// host is held in <see cref="Exception.InnerException" /> 
    /// and in <see cref="ExceptionPayload" />.
    /// </remarks>
    public sealed class RemoteWorkException : Exception
    {
        /// <summary>
        /// Construct the .NET exception representation an error
        /// received from a remote worker host.
        /// </summary>
        /// <param name="payload">
        /// The serializable form of the exception data
        /// that the underlying RPC protocol uses.
        /// </param>
        public RemoteWorkException(ExceptionPayload payload)
            : base("A remote worker host replied to a request with an error. ",
                   new InnerExceptionImpl(payload))
        {
            Payload = payload;
        }

        /// <summary>
        /// The serializable form of the exception data
        /// received from the remote host.
        /// </summary>
        public ExceptionPayload Payload { get; }

        /// <summary>
        /// The inner exception type for <see cref="RemoteWorkException" />
        /// that reports information 
        /// </summary>
        private sealed class InnerExceptionImpl : Exception
        {
            private readonly ExceptionPayload _payload;

            internal InnerExceptionImpl(ExceptionPayload payload) => _payload = payload;

            /// <inheritdoc />
            public override string? Source
            {
                get => _payload.Source;
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override string? StackTrace => _payload.Trace;

            /// <inheritdoc />
            public override string Message => _payload.Description ?? String.Empty;
        }
    }
}
