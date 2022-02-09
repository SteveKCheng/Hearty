using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using System;
using Xunit;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using JobBank.Server.Program;
using JobBank.Server.Mocks;
using JobBank.Client;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.IO;

namespace JobBank.Server.Tests
{
    public class PromiseApiTests : IDisposable
    {
        private readonly TestServer _webServer;
        private readonly Encoding _utf8 = new UTF8Encoding(false);

        public const string PathBase = "/test/";

        public PromiseApiTests()
        {
            var webBuilder = new WebHostBuilder();
            webBuilder.ConfigureAppConfiguration(configBuilder =>
            {
                configBuilder.AddInMemoryCollection(new KeyValuePair<string, string>[]
                {
                    new("enableUi", "false"),
                    new("pathBase", PathBase)
                });
            });

            webBuilder.UseStartup<Startup>();

            _webServer = new TestServer(webBuilder);
        }

        private HttpClient CreateClient()
        {
            var client = _webServer.CreateClient();
            client.BaseAddress = new Uri(client.BaseAddress!, PathBase);
            return client;
        }

        [Fact]
        public async Task RunJob()
        {
            var itemList = new List<(MockPricingInput Input, PromiseId PromiseId)>();

            using var client = new JobBankClient(CreateClient());
            var inputs = MockPricingInput.GenerateRandomSamples(DateTime.Today, 41, 5);

            foreach (var input in inputs)
            {
                var content = new ByteArrayContent(input.SerializeToJsonUtf8Bytes());
                content.Headers.ContentType = new("application/json");
                var promiseId = await client.PostJobAsync("pricing", content);
                Assert.NotEqual((ulong)0, promiseId.RawInteger);

                itemList.Add((input, promiseId));
            }

            foreach (var (input, promiseId) in itemList)
            {
                var stream = await client.GetContentAsync(promiseId,
                                                          contentType: "application/json",
                                                          timeout: TimeSpan.FromMinutes(5));

                var output = MockPricingOutput.DeserializeFromStream(stream);

                var expected = input.Calculate();
                Assert.Equal(expected, output);
            }
        }

        // Unfinished: to expand into a test of streaming.
        public void RunJobList()
        {
            var itemList = new List<(MockPricingInput Input, PromiseId PromiseId)>();

            using var client = new JobBankClient(CreateClient());
            var inputs = MockPricingInput.GenerateRandomSamples(DateTime.Today, 41, 50);

            using (var file = File.OpenWrite("JobList.json"))
            {
                JsonSerializer.Serialize(file, inputs, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
        }

        public void Dispose()
        {
            _webServer.Dispose();
        }
    }
}
