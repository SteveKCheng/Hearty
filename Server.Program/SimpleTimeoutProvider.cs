using System;
using JobBank.Scheduling;

namespace JobBank.Server.Program
{
    internal sealed class SimpleTimeoutProvider : TimeoutProvider, IDisposable
    {
        private readonly SimpleExpiryQueue[] _expiryQueues;

        public SimpleTimeoutProvider()
        {
            _expiryQueues = new SimpleExpiryQueue[NumberOfBuckets];
            for (int i = 0; i < _expiryQueues.Length; ++i)
                _expiryQueues[i] = new SimpleExpiryQueue(GetMillisecondsForBucket((TimeoutBucket)i), 20);
        }

        public void Dispose()
        {
            foreach (var queue in _expiryQueues)
                queue.Dispose();
        }

        public override void Register(TimeoutBucket bucket, ExpiryAction action, object? state)
        {
            _expiryQueues[(int)bucket].Enqueue(action, state);
        }
    }
}
