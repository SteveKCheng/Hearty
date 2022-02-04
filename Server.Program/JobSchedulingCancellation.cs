using JobBank.Server.WebApi;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JobBank.Server.Program
{
    public sealed class JobSchedulingCancellation : IRemoteCancellation<PromiseId>
    {
        private readonly JobSchedulingSystem _jobScheduling;

        public JobSchedulingCancellation(JobSchedulingSystem jobScheduling)
            => _jobScheduling = jobScheduling;

        public ValueTask<CancellationStatus> TryCancelAsync(ClaimsPrincipal client, PromiseId target, bool force)
        {
            var success = _jobScheduling.TryCancelForClient(Startup._dummyQueueOwner, target);
            return ValueTask.FromResult(success ? CancellationStatus.Cancelled 
                                                : CancellationStatus.NotFound);
        }
    }
}
