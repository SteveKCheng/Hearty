using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JobBank.Server
{
    public interface IPromiseClientInfo
    {
        /// <summary>
        /// Called when this client is about to subscribe to a promise.
        /// </summary>
        /// <remarks>
        /// This method is called before the subscription node object
        /// gets attached to the promise. 
        /// </remarks>
        /// <param name="index">The value set here is assigned to
        /// <see cref="Subscription.Index"/>.  Usually it is a sequence
        /// number maintained by a client, so that its subscriptions
        /// can be individually distinguished.  If there can be no
        /// concurrent look-ups of subscriptions by index, then values
        /// can even be re-used.
        /// </param>
        void OnSubscribe(Subscription subscription, out uint index);

        /// <summary>
        /// Called to notify this client when a subscription to a promise
        /// is taken off. 
        /// </summary>
        /// <remarks>
        /// This method is called after the subscription node object
        /// gets detached from the promise. 
        /// </remarks>
        void OnUnsubscribe(Subscription subscription);

        /// <summary>
        /// Name of the user that this client is associated to.
        /// </summary>
        string UserName { get; }
    }
}
