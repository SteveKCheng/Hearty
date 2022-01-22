using System.Net.Http;
using System.Threading.Tasks;

namespace JobBank.Client
{
    public class JobBankClient
    {
        private readonly HttpClient _httpClient;

        public JobBankClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task PostJobAsync(string routeKey, HttpContent content)
        {
            await _httpClient.PostAsync("jobs/v1/queue/" + routeKey, content)
                             .ConfigureAwait(false); 
        }
    }
}