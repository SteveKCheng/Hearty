using System;
using System.Collections.Concurrent;
using System.Threading;

namespace JobBank.Server
{
    /// <summary>
    /// Basic implementation of <see cref="PromiseStorage" />
    /// using in-process managed memory only.
    /// </summary>
    public class BasicPromiseStorage : PromiseStorage
    {
        private readonly ConcurrentDictionary<PromiseId, Promise>
            _promisesById = new ConcurrentDictionary<PromiseId, Promise>();

        private ulong _currentId;

        /// <summary>
        /// Prepare to storage <see cref="Promise" /> objects
        /// entirely in managed memory.
        /// </summary>
        public BasicPromiseStorage()
        {
            _currentId = 0;
        }

        /// <inheritdoc />
        public override Promise CreatePromise()
        {
            var newId = new PromiseId(Interlocked.Increment(ref _currentId));
            var promise = new Promise(newId);

            if (!_promisesById.TryAdd(newId, promise))
                throw new InvalidOperationException("The promise with the newly generated ID already exists.  This should not happen. ");

            return promise;
        }

        /// <inheritdoc />
        public override Promise? GetPromiseById(PromiseId id)
        {
            return _promisesById.TryGetValue(id, out var promise) 
                        ? promise 
                        : null;
        }

        /// <inheritdoc />
        public override void SchedulePromiseExpiry(Promise promise, DateTime when)
        {
            throw new NotImplementedException();
        }
    }
}
