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
                services.AddSingleton<WorkerFactory>((IServiceProvider p) =>
                    (message, rpc) => new MockPricingWorker(p.GetRequiredService<ILogger<MockPricingWorker>>(), message.Name));
                services.AddSingleton<WorkerHostServiceSettings>(p =>
                    p.GetRequiredService<IConfiguration>().Get<WorkerHostServiceSettings>());

                services.AddHostedService<WorkerHostService>();
            })
            .UseConsoleLifetime()
            .Build();

        return host.RunAsync();
    }
}
