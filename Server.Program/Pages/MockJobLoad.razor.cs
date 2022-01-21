using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server.Program.Pages
{
    public partial class MockJobLoad
    {
        private readonly IJobQueueOwner[] _clients = new IJobQueueOwner[3]
        {
            new SimpleQueueOwner("Alice"),
            new SimpleQueueOwner("Bobby"),
            new SimpleQueueOwner("Charlie")
        };

        private IJobQueueOwner GetClient(int index) => _clients[index];

        private void InjectJobs(int client, int priority, int load)
        {
            PromiseData request =
                new Payload("application/json", Encoding.ASCII.GetBytes(@"{ ""request"": ""..."" }"));

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

                var promise = _promiseStorage.CreatePromise(request);
                var jobFunction = new PromiseJob(request);
                _jobScheduling.PushJobForClientAsync(GetClient(client),
                                                     priority,
                                                     waitingTime,
                                                     promise,
                                                     jobFunction);
            }
        }

        private readonly Func<double>[] _waitingTimeGenerators;

        public MockJobLoad()
        {
            _waitingTimeGenerators = new Func<double>[3];
            _waitingTimeGenerators[0] = CreateExponentialWaitingTimeGenerator(34, 200.0, 8000.0);
            _waitingTimeGenerators[1] = CreateExponentialWaitingTimeGenerator(34, 500.0, 8000.0);
            _waitingTimeGenerators[2] = CreateExponentialWaitingTimeGenerator(34, 2000.0, 8000.0);
        }

        private static Func<double>
            CreateExponentialWaitingTimeGenerator(int seed, double mean, double cap)
        {
            var uniformGenerator = new Random(seed);
            return () =>
            {
                var y = uniformGenerator.NextDouble();
                var t = -Math.Log(1.0 - y) * mean;
                t = Math.Min(t, cap);
                return t;
            };
        }
    }
}
