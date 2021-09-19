using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// A basic implementation of <see cref="IPromiseClientInfo" />.
    /// </summary>
    public class BasicPromiseClientInfo : IPromiseClientInfo
    {
        private uint _subscriptionCount;

        public string UserName => "current-user";

        public ClaimsPrincipal? User => null;

        public uint OnSubscribe(Subscription subscription)
        {
            return Interlocked.Increment(ref _subscriptionCount);
        }

        public void OnUnsubscribe(Subscription subscription, uint index)
        {
        }
    }
}
