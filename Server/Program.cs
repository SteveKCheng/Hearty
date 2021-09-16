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
                jobsController.MapHttpRoutes(endpoints, "pricing", async () => {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    return new Payload("application/json", Encoding.ASCII.GetBytes(@"{ ""status"": ""completed"" }"));
                });
            });

            app.Run();
        }
    }
}
