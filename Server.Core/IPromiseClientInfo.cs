using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Hearty.Server
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
        /// <returns>A sequence number or index maintained by a client, 
        /// that can distinguish subscriptions by the same client.  If 
        /// there can be no concurrent look-ups of subscriptions by index, 
        /// then the index values can be re-used.
        /// </returns>
        uint OnSubscribe(Subscription subscription);

        /// <summary>
        /// Called to notify this client when a subscription to a promise
        /// is taken off. 
        /// </summary>
        /// <param name="subscription">The same subscription that was
        /// passed to <see cref="OnSubscribe"/> 
        /// when it had returned <paramref name="index"/>.
        /// </param>
        /// <param name="index">The previously returned value
        /// from <see cref="OnSubscribe"/> that indicates which
        /// subscription is to be removed.
        /// </param>
        /// <remarks>
        /// This method is called after the subscription node object
        /// gets detached from the promise. 
        /// </remarks>
        void OnUnsubscribe(Subscription subscription, uint index);

        /// <summary>
        /// Name of the user that this client is associated to.
        /// </summary>
        string UserName { get; }

        /// <summary>
        /// Represents the user for authorizing operations on promises. 
        /// </summary>
        /// <remarks>
        /// Null means that the requesting user is unknown or anonymous.
        /// </remarks>
        ClaimsPrincipal? User { get; }
    }
}
