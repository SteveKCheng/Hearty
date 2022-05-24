using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Hearty.Work;

namespace Hearty.Server.Mocks;

internal static class Program
{
    public static Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddHostedService<WorkerHostService>(p =>
                {
                    var config = p.GetRequiredService<IConfiguration>();
                    var settings = config.Get<WorkerHostServiceSettings>();
                    var logger = p.GetRequiredService<ILogger<MockPricingWorker>>();
                    return new WorkerHostService(settings,
                                                 (message, rpc) => new MockPricingWorker(logger, message.Name), 
                                                 p.GetRequiredService<ILogger<WorkerHostService>>(),
                                                 rpcRegistry: null);
                });
            })
            .UseConsoleLifetime()
            .Build();

        return host.RunAsync();
    }
}
