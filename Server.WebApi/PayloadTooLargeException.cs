using System;

#pragma warning disable CS1591

namespace JobBank.Server.WebApi
{
    public class PayloadTooLargeException : Exception
    {
        public PayloadTooLargeException()
            : this("The payload in the HTTP request exceeds the maximum allowed. ")
        {
        }

        public PayloadTooLargeException(string? message) : base(message)
        {
        }

        public PayloadTooLargeException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}
