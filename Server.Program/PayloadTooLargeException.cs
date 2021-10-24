using System;

namespace JobBank.Server
{
    internal class PayloadTooLargeException : Exception
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
