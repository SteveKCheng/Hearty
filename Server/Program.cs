using Microsoft.AspNetCore.Builder;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using System;
using System.Text;

namespace JobBank.Server
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            var jobsController = new JobsController();

            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", () => "Hello World!");
                jobsController.MapHttpRoutes(endpoints, "pricing", (JobInput input, Promise promise) => {
                    
                    static async ValueTask<PromiseResult> MockWork()
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        return PromiseResult.CreatePayload("application/json", Encoding.ASCII.GetBytes(@"{ ""status"": ""completed"" }"));
                    }

                    return ValueTask.FromResult(new Job(MockWork())
                    {
                        PromiseId = "x",
                    });
                });
            });

            app.Run();
        }
    }
}
