using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Holds the result (object) from <see cref="Promise"/>
    /// and maintains the client's subscription until disposed.
    /// </summary>
    public struct PromiseResult : IDisposable
    {
        private Promise.SubscriptionNode? _subscription;

        public Payload Payload { get; }

        internal PromiseResult(Promise.SubscriptionNode? subscription, in Payload payload)
        {
            _subscription = subscription;
            Payload = payload;
        }

        /// <summary>
        /// Unsubscribes from the promise.
        /// </summary>
        /// <remarks>
        /// The other members of this structure should not be accessed
        /// after disposal.
        /// </remarks>
        public void Dispose()
        {
            var subscription = _subscription;
            if (subscription != null)
            {
                _subscription = null;
                subscription.DetachSelf();
            }
        }
    }
}
