using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hearty.Server;

/// <summary>
/// Represents a subscription from a client to a promise.
/// </summary>
/// <remarks>
/// <para>
/// When retrieving results asynchronously from a promise,
/// the Hearty framework requires an active subscription.
/// The act of subscription records information about the client 
/// (consumer) to facilitate monitoring and administration.
/// </para>
/// <para>
/// Internally, each subscription is represented by a node
/// in a linked list.  This structure deliberately hides
/// this implementation detail, which may change. 
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
}
