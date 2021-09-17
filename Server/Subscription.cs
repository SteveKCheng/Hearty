using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Represents a subscription from a client to a promise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When retrieving results asynchronously from a promise,
    /// the Job Bank framework requires an active subscription.
    /// The act of subscription records information about the client 
    /// (consumer) to facilitate monitoring and administration.
    /// </para>
    /// <para>
    /// This structure wraps <see cref="Promise.SubscriptionNode" /> so that the interfaces
    /// it implements for internal use do not leak out to the public API.
    /// </para>
    /// <para>
    /// This structure should be not be default-initialized.
    /// If such instances are attempted to be used, <see cref="NullReferenceException" />
    /// gets thrown.
    /// </para>
    /// </remarks>
    public readonly struct Subscription
    {
        /// <summary>
        /// The internal node object for the subscription.
        /// </summary>
        private readonly Promise.SubscriptionNode _node;

        /// <summary>
        /// Wraps an internal node object.
        /// </summary>
        internal Subscription(Promise.SubscriptionNode node) => _node = node;

        /// <summary>
        /// The promise being subscribed to.
        /// </summary>
        public Promise Promise => _node.Promise;

        /// <summary>
        /// The client that is subscribing to the promise.
        /// </summary>
        public IPromiseClientInfo Client => _node.Client;

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
        public uint Index => _node.Index;
    }
}
