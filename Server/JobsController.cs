using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using System.Threading;

namespace JobBank.Server
{
    public class JobsController
    {
        private ConcurrentDictionary<string, Promise> _promisesByKey
            = new ConcurrentDictionary<string, Promise>();



        private ulong _currentId = 0;

        private readonly IMemoryCache _cache;

        public JobsController()
        {
            _cache = new MemoryCache(new MemoryCacheOptions
            {
            });
        }

        internal Promise CreatePromise(string key, out string id)
        {
            var promise = new Promise();

            var idNum = Interlocked.Increment(ref _currentId);
            id = idNum.ToString();

            _cache.Set(id, promise);
            /*
            var newPromise = new Promise(requestPayload);
            var promise = _promisesByKey.GetOrAdd(key, newPromise);
            if (!object.ReferenceEquals(promise, newPromise))
            {
                if (!promise.RequestPayload.Equals(requestPayload))
                {
                    throw new PromiseException($"Request payload for promise with {key} differs from already existing promise. ");
                }
            }
            */

            return promise;
        }

        internal Promise? GetPromiseById(string id)
        {
            _cache.TryGetValue<Promise>(id, out var promise);
            return promise;
        }
    }
}
