using Microsoft.AspNetCore.Builder;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using System;
using System.Text;
using System.IO;
using System.Threading;

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
                jobsController.MapHttpRoutes(endpoints, "pricing", async (JobInput input, Promise promise) => 
                {
                    using var stream = input.PipeReader.AsStream();
                    Memory<byte> requestData = await ReadStreamIntoMemorySafelyAsync(stream, 
                                                                                     input.ContentLength, 
                                                                                     16 * 1024 * 1024, 
                                                                                     input.CancellationToken);
                    static async ValueTask<PromiseOutput> MockWork()
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        return new Payload("application/json", Encoding.ASCII.GetBytes(@"{ ""status"": ""completed"" }"));
                    }

                    return new Job(MockWork())
                    {
                        PromiseId = "x",
                    };
                });
            });

            app.Run();
        }

        /// <summary>
        /// Read a (non-seekable) stream into memory, applying limits on the amount of data.
        /// </summary>
        private static async Task<Memory<byte>>
            ReadStreamIntoMemorySafelyAsync(Stream stream, 
                                            long? streamLength, 
                                            int lengthLimit, 
                                            CancellationToken cancellationToken)
        {
            if (streamLength > lengthLimit)
                throw new PayloadTooLargeException();

            var payload = new byte[(streamLength + 1) ?? 8092];

            int bytesTotalRead = 0;

            while (true)
            {
                int bytesJustRead = await stream.ReadAsync(new Memory<byte>(payload).Slice(bytesTotalRead),
                                                           cancellationToken);
                if (bytesJustRead == 0)
                    break;

                bytesTotalRead += bytesJustRead;

                if (streamLength != null)
                {
                    if (bytesTotalRead > streamLength.Value)
                        throw new PromiseException("Got more bytes of payload than what the HTTP header Content-Length indicated. ");
                }
                else
                {
                    if (payload.Length - bytesTotalRead < payload.Length / 4)
                    {
                        var newPayload = new byte[payload.Length * 2];
                        payload.AsSpan().Slice(0, bytesTotalRead).CopyTo(newPayload);
                        payload = newPayload;
                    }
                }
            }

            return new Memory<byte>(payload, 0, bytesTotalRead);
        }
    }
}
