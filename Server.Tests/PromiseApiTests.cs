using JobBank.Server.Program;
using JobBank.Client;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using System;
using Xunit;
using System.Text;
using System.Threading.Tasks;

namespace JobBank.Server.Tests
{
    public class PromiseApiTests : IDisposable
    {
        private readonly TestServer _webServer;
        private readonly Encoding _utf8 = new UTF8Encoding(false);

        public PromiseApiTests()
        {
            var webBuilder = new WebHostBuilder();
            webBuilder.UseStartup<Startup>();

            _webServer = new TestServer(webBuilder);
            _webServer.CreateClient();
        }

        [Fact]
        public async Task RunJob()
        {
            using var client = new JobBankClient(_webServer.CreateClient());
            var input = @"{ ""test"": ""test"" }";
            var content = new ByteArrayContent(_utf8.GetBytes(input));
            content.Headers.ContentType = new("application/json");
            var promiseId = await client.PostJobAsync("pricing", content);
            Assert.NotEqual((ulong)0, promiseId.RawInteger);
        }

        public void Dispose()
        {
            _webServer.Dispose();
        }
    }
}
