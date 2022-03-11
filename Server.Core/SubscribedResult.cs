using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hearty.Server
{
    /// <summary>
    /// Holds the result (object) from <see cref="Promise"/>
    /// and maintains the client's subscription until disposed.
    /// </summary>
    /// <remarks>
    /// When this structure gets used inside <see cref="ValueTask{PromiseResult}" />,
    /// the client's subscription remains active even when the result is available
    /// immediately.  This behavior is deliberate.  Firstly, the "result" can be
    /// an additional object like a pipe reader that can have more
    /// asynchronous operations requested.   Secondly, sending the results to
    /// the client (over a network) is usually asynchronous also, and for more
    /// precise monitoring, it is best if the subscription stays open until the data
    /// gets completely sent (to the best approximation).  Thus the scope of the
    /// subscription does not end as soon as awaiting the promise result ends.
    /// </remarks>
    public struct SubscribedResult : IDisposable
    {
        private Promise.SubscriptionNode? _subscription;

        /// <summary>
        /// The result of the promise made available in various forms,
        /// or null if there is some error that makes the output unavailable.
        /// </summary>
        public PromiseData? Output { get; }

        /// <summary>
        /// Status of the promise.
        /// </summary>
        public PromiseStatus Status { get; }

        /// <summary>
        /// The result of the promise made available in various forms.
        /// </summary>
        /// <remarks>
        /// If the normal output is not available because of some error or exception,
        /// accessing this property throws an exception.
        /// </remarks>
        public PromiseData NormalOutput
        {
            get
            {
                switch (Status)
                {
                    case PromiseStatus.Published: 
                        return Output!;
                    case PromiseStatus.ClientCancelled:
                        if (_subscription != null)
                            _subscription.CancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException();
                    case PromiseStatus.ClientTimedOut:
                        throw new TimeoutException("Data for the promise was not available before timing out. ");
                    default:
                        throw new PromiseException("Error retrieving result for promise. ");
                }
            }
        }
 
        internal SubscribedResult(Promise.SubscriptionNode subscription, PromiseData output)
        {
            _subscription = subscription;
            Output = output;
            Status = PromiseStatus.Published;
        }

        internal SubscribedResult(Promise.SubscriptionNode subscription, PromiseStatus status)
        {
            _subscription = subscription;
            Output = null;
            Status = status;
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
    }

    public enum PromiseStatus
    {
        InternalError,
        Published,
        Faulted,
        ClientCancelled,
        ClientTimedOut
    }

}
