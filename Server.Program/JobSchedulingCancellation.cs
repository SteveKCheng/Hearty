using JobBank.Server.WebApi;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JobBank.Server.Program
{
    public sealed class JobSchedulingCancellation : IRemoteCancellation<PromiseId>
    {
        private readonly JobsManager _jobManager;

        public JobSchedulingCancellation(JobsManager jobsManager)
            => _jobManager = jobsManager;

        public ValueTask<CancellationStatus> TryCancelAsync(ClaimsPrincipal client, PromiseId target, bool force)
        {
            var queueKey = new JobQueueKey(Startup._dummyQueueOwner, 5, string.Empty);
            var success = _jobManager.TryCancelForClient(queueKey, target);
            return ValueTask.FromResult(success ? CancellationStatus.Cancelled 
                                                : CancellationStatus.NotFound);
        }
    }
}
