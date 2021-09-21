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
    /// <remarks>
    /// When this structure gets used inside <see cref="ValueTask{PromiseResult}" />,
    /// the client's subscription remains active even when the result is available
    /// immediately.  This behavior is deliberate.  Firstly, the "result" can be
    /// an additional object like <see cref="PipeReader" /> that can have more
    /// asynchronous operations requested.   Secondly, sending the results to
    /// the client (over a network) is usually asynchronous also, and for more
    /// precise monitoring, it is best if the subscription stays open until the data
    /// gets completely sent (to the best approximation).  Thus the scope of the
    /// subscription does not end as soon as awaiting the promise result ends.
    /// </remarks>
    public struct PromiseResult : IDisposable
    {
        private Promise.SubscriptionNode? _subscription;

        /// <summary>
        /// The result of the promise made available in various forms.
        /// </summary>
        public PromiseOutput Output { get; }

        internal PromiseResult(Promise.SubscriptionNode? subscription, PromiseOutput output)
        {
            _subscription = subscription;
            Output = output;
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
                subscription.Dispose();
            }
        }

        public static PromiseResult CreatePayload(string contentType, Memory<byte> data)
            => new PromiseResult(null, new Payload(contentType, data));
    }
}
