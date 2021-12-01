using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JobBank.Server.Program.Data;
using JobBank.Utilities;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Text;
using Microsoft.Extensions.Logging;

namespace JobBank.Server.Program
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddSingleton<WeatherForecastService>();

            services.AddSingleton<PromiseStorage>(c =>
            {
                var logger = c.GetRequiredService<ILogger<PromiseStorage>>();

                var promiseStorage = new BasicPromiseStorage();
                promiseStorage.OnStorageEvent += (sender, args) =>
                {
                    string message = args.Type switch
                    {
                        PromiseStorage.OperationType.Create => "Created promise ID {id}",
                        PromiseStorage.OperationType.ScheduleExpiry => "Scheduled expiry for promise ID {id}",
                        _ => "Unknown event for promise ID {id}"
                    };

                    logger.LogInformation(message, args.PromiseId);
                };

                return promiseStorage;
            });

            services.AddSingleton<PathsDirectory, InMemoryPathsDirectory>();
            services.AddSingleton<JobsServerConfiguration>();
            services.AddSingleton<JobSchedulingSystem>();
            services.AddSingleton<TimeoutProvider, SimpleTimeoutProvider>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();
           
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
                
                var jobsServerConfig = app.ApplicationServices.GetRequiredService<JobsServerConfiguration>();
                var jobScheduling = app.ApplicationServices.GetRequiredService<JobSchedulingSystem>();

                endpoints.MapPostJob(jobsServerConfig, "pricing", async (JobInput input, PromiseStorage promiseStorage) =>
                {
                    using var stream = input.PipeReader.AsStream();
                    var requestData = await ReadStreamIntoMemorySafelyAsync(stream,
                                                                            input.ContentLength,
                                                                            16 * 1024 * 1024,
                                                                            8192,
                                                                            input.CancellationToken);

                    var request = new Payload(input.ContentType ?? string.Empty, requestData);
                    
                    var outputTask = jobScheduling.PushJobForClientAsync("testClient", 5, request, 15000);

                    return promiseStorage.CreatePromise(request, outputTask).Id;
                });

                endpoints.MapGetPromiseById(jobsServerConfig);
                endpoints.MapGetPromiseByPath(jobsServerConfig);
            });
        }

        /// <summary>
        /// Read a (non-seekable) stream into a set of buffers in memory, 
        /// applying limits on the amount of data.
        /// </summary>
        private static async Task<ReadOnlySequence<byte>>
            ReadStreamIntoMemorySafelyAsync(Stream stream,
                                            long? streamLength,
                                            int lengthLimit,
                                            int initialBufferSize,
                                            CancellationToken cancellationToken)
        {
            if (streamLength > lengthLimit)
                throw new PayloadTooLargeException();

            SegmentedArrayBufferWriter<byte> bufferWriter;

            // Pre-allocate for the supposed length if known, unless it is too large.
            // There needs to be one extra byte in the buffer, unless the length
            // calculation overflows, to reliably determine when Stream.ReadAsync
            // reaches the end of the stream.
            if (streamLength is long n)
            {
                int m = (int)n;
                bufferWriter = new(1, 0);
                bufferWriter.GetMemory(m + (m != int.MaxValue ? 1 : 0));
            }
            else
            {
                bufferWriter = new(initialBufferSize, 1);
            }

            uint bytesTotalRead = 0;
            int bytesJustRead;

            while ((bytesJustRead = await stream.ReadAsync(bufferWriter.GetMemory(), cancellationToken)
                                                .ConfigureAwait(false)) > 0)
            {
                bytesTotalRead += (uint)bytesJustRead;

                if (bytesTotalRead > streamLength)
                    throw new InvalidDataException("Received more bytes from the input stream than what had been expected beforehand. ");

                if (bytesTotalRead > lengthLimit)
                    throw new PayloadTooLargeException();

                bufferWriter.Advance(bytesJustRead);
            }

            if (streamLength != null && (int)bytesTotalRead != (int)streamLength)
                throw new InvalidDataException("Received fewer bytes from the input stream than what had been expected beforehand. ");

            return bufferWriter.GetSequence();
        }

    }
}
