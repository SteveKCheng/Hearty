using Hearty.Server.Mocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Server.Program.Pages
{
    public partial class MockJobLoad
    {
        private readonly IJobQueueOwner[] _clients = new IJobQueueOwner[3]
        {
            new JobQueueOwner("Alice"),
            new JobQueueOwner("Bobby"),
            new JobQueueOwner("Charlie")
        };

        private IJobQueueOwner GetClient(int index) => _clients[index];

        private void InjectJobs(int client, int priority, int load)
        {
            int howMany = load switch
            {
                0 => 100,
                1 => 60,
                _ => 20
            };

            var waitingTimeGenerator = _waitingTimeGenerators[load];

            for (int i = 0; i < howMany; ++i)
            {
                int waitingTime = (int)waitingTimeGenerator();

                var requestJson = MockPricingInput.GenerateRandomSample(DateTime.Today, _random)
                                                  .SerializeToJsonUtf8Bytes();

                PromiseData request = new Payload("application/json", requestJson);

                var promise = _promiseStorage.CreatePromise(request);
                var work = new PromisedWork(request) { InitialWait = waitingTime, Promise = promise };
                var queueKey = new JobQueueKey(GetClient(client), priority, string.Empty);
                _jobsManager.PushJob(queueKey,
                                     static w => w.Promise ?? throw new ArgumentNullException(),
                                     work);
            }
        }

        private readonly Func<double>[] _waitingTimeGenerators;
        private readonly Random _random = new Random(34);

        public MockJobLoad()
        {
            _waitingTimeGenerators = new Func<double>[3];
            _waitingTimeGenerators[0] = CreateExponentialWaitingTimeGenerator(_random, 200.0, 8000.0);
            _waitingTimeGenerators[1] = CreateExponentialWaitingTimeGenerator(_random, 500.0, 8000.0);
            _waitingTimeGenerators[2] = CreateExponentialWaitingTimeGenerator(_random, 2000.0, 8000.0);
        }

        private static Func<double>
            CreateExponentialWaitingTimeGenerator(Random random, double mean, double cap)
        {
            return () =>
            {
                var y = random.NextDouble();
                var t = -Math.Log(1.0 - y) * mean;
                t = Math.Min(t, cap);
                return t;
            };
        }
    }
}
