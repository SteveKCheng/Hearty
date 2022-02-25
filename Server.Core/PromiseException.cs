using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Hearty.Server
{
    public class PromiseException : Exception
    {
        public PromiseException()
        {
        }

        public PromiseException(string? message) : base(message)
        {
        }

        public PromiseException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected PromiseException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
