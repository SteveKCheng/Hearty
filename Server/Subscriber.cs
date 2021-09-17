using System;

namespace JobBank.Server
{
    /// <summary>
    /// Coordinates identifying a subscriber to a promise.
    /// </summary>
    /// <remarks>
    /// When retrieving results asynchronously from a promise,
    /// the Job Bank framework requires an active subscription.
    /// The act of subscription records information about the client 
    /// (consumer) to facilitate monitoring and administration.
    /// </remarks>
    public readonly struct Subscriber
    {
        /// <summary>
        /// The client that is subscribing to the promise.
        /// </summary>
        public IPromiseClientInfo Client { get; }

        /// <summary>
        /// An arbitrary integer that the client can associate to the subscribed promise, 
        /// so that it can distinguish its other subscriptions.
        /// </summary>
        /// <remarks>
        /// One client may subscribe to many promises.  We do not identify each
        /// individual subscriptions as abstract "clients" because 
        /// real clients need to be associated with users or
        /// connections (for authentication and monitoring), which tend
        /// to be heavier objects.
        /// </remarks>
        public int Index { get; }

        /// <summary>
        /// Packages information describing a new consumer of a promise.
        /// </summary>
        /// <param name="client">See <see cref="Client"/>. </param>
        /// <param name="index">See <see cref="Index"/>. </param>
        public Subscriber(IPromiseClientInfo client, int index)
        {
            Client = client;
            Index = index;
        }
    }
}
