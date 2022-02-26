using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using System;
using Xunit;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Hearty.Server.Program;
using Hearty.Server.Mocks;
using Hearty.Client;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.IO;
using System.Threading;
using System.Linq;
using Hearty.Common;

namespace Hearty.Server.Tests
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

            using var client = new HeartyClient(CreateClient());
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

        [Fact]
        public async Task RunJobList()
        {
            using var client = new HeartyClient(CreateClient());
            var inputs = MockPricingInput.GenerateRandomSamples(DateTime.Today, 41, 50)
                                         .ToList();

            var content = new StreamWriterContent(stream => JsonSerializer.SerializeAsync(stream, inputs));

            var promiseId = await client.PostJobAsync("multi", content);

            var responses = await client.GetItemStreamAsync(promiseId,
                                                            contentType: "application/json",
                                                            DeserializeMockPricingOutput,
                                                            default);

            var ordinalsSeen = new HashSet<int>();

            await foreach (var (ordinal, result) in responses)
            {
                Assert.InRange(ordinal, 0, inputs.Count - 1);
                Assert.True(ordinalsSeen.Add(ordinal));

                var input = inputs[ordinal];
                var expected = input.Calculate();
                Assert.Equal(expected, result);
            }
        }

        private static ValueTask<MockPricingOutput> 
            DeserializeMockPricingOutput(ParsedContentType contentType, 
                                         Stream stream, 
                                         CancellationToken cancellationToken)
        {
            Assert.True(contentType.IsSubsetOf(new ParsedContentType("application/json")));

            return JsonSerializer.DeserializeAsync<MockPricingOutput>(stream, cancellationToken: cancellationToken);

            /*
            var memStream = new MemoryStream();
            await stream.CopyToAsync(memStream);
            memStream.Position = 0;
            var s = new StreamReader(memStream, Encoding.UTF8).ReadToEnd();
            var output = JsonSerializer.Deserialize<MockPricingOutput>(s);
            return output;
            */
        }

        [Fact]
        public async Task BearerTokenRetrieval()
        {
            using var client = new HeartyClient(CreateClient());
            await client.SignInAsync("admin", "admin");
            Assert.NotNull(client.BearerToken);
        }

        public void Dispose()
        {
            _webServer.Dispose();
        }
    }
}
