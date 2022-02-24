using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JobBank.Utilities;
using System.Buffers;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using JobBank.Server.Mocks;
using JobBank.Server.WebApi;
using JobBank.Scheduling;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using idunno.Authentication.Basic;
using System.Security.Claims;

namespace JobBank.Server.Program
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;

            UiEnabled = configuration.GetValue<bool>("enableUi", true);
            PathBase = configuration.GetValue<string>("pathBase", string.Empty);
        }

        public bool UiEnabled { get; }

        public PathString PathBase { get; }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();

            if (UiEnabled)
            {
                services.AddHttpContextAccessor();
                services.AddRazorPages();
                services.AddServerSideBlazor();
            }

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
            services.AddSingleton<JobsManager>();
            services.AddSingleton<TimeoutProvider, SimpleTimeoutProvider>();
            services.AddSingleton<WorkerDistribution<PromisedWork, PromiseData>>(p =>
            {
                var d = new WorkerDistribution<PromisedWork, PromiseData>();

                var w = new MockPricingWorker(
                            logger: p.GetRequiredService<ILogger<MockPricingWorker>>(),
                            name: "local-worker");
                
                d.TryCreateWorker(new LocalWorkerAdaptor(w, w.Name) , 10, out _);
                return d;
            });
            services.AddSingleton<IJobQueueSystem>(
                p => new JobQueueSystem(
                            countPriorities: 10, 
                            p.GetRequiredService<WorkerDistribution<PromisedWork, PromiseData>>()));

            services.AddSingleton<IRemoteCancellation<PromiseId>, JobSchedulingCancellation>();
            services.AddSingleton<PromiseExceptionTranslator>(BasicExceptionTranslator.Instance);

            services.AddSingleton<MockWorkerHosts>();

            services.AddSingleton<JobQueueOwnerRetriever>((ClaimsPrincipal? principal, string? id) =>
            {
                var name = principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
                return new ValueTask<IJobQueueOwner?>(new JobQueueOwner(name, principal));
            });

            var authBuilder = services.AddAuthentication(
                                JwtBearerDefaults.AuthenticationScheme);

            var jwtPassphrase = Configuration.GetValue<string?>("JsonWebToken:Passphrase");
            authBuilder.AddSelfIssuedJwtBearer(jwtPassphrase);

            authBuilder.AddBasic(options =>
            {
                // N.B. Basic Authentication is only used for retrieving JSON Web Tokens.
                //      Other endpoints requiring authorization require JSON Web Tokens
                //      pass as "Bearer" tokens.
                options.AllowInsecureProtocol = true;
                        options.Events = new BasicAuthenticationEvents
                        {
                            OnValidateCredentials = context =>
                            {
                                if (context.Username == context.Password)
                                {
                                    var claims = new Claim[]
                                    {
                                new(ClaimTypes.NameIdentifier, context.Username, ClaimValueTypes.String, context.Options.ClaimsIssuer),
                                new(ClaimTypes.Name, context.Username, ClaimValueTypes.String, context.Options.ClaimsIssuer)
                                    };

                                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
                                    context.Success();
                                }

                                return Task.CompletedTask;
                            }
                        };
            });

            authBuilder.AddCookie(options =>
            {
                options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
                options.SlidingExpiration = true;
                options.AccessDeniedPath = "/";
            });

            services.AddAuthorization(options =>
            {
                options.AddPolicy("basic", policy =>
                {
                    policy.AddAuthenticationSchemes(BasicAuthenticationDefaults.AuthenticationScheme);
                    policy.RequireAuthenticatedUser();
                });
            });

        }

        internal static readonly IJobQueueOwner _dummyQueueOwner =
            new JobQueueOwner("testClient");

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseStrictPathBaseOrRedirect(PathBase);

            app.UseForwardedHeaders();

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

            // Do not just add arguments to this call.  Blazor will break if you do:
            // https://github.com/dotnet/aspnetcore/issues/19578
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseWebSockets();

            app.UseEndpoints(endpoints =>
            {
                if (UiEnabled)
                {
                    endpoints.Map("/", httpContext =>
                    {
                        httpContext.Response.Redirect(PathBase.Add("/ui/"), false);
                        return Task.CompletedTask;
                    });

                    endpoints.MapBlazorHub("/ui/_blazor");
                    endpoints.MapFallbackToPage(pattern: "/ui/{*path:nonfile}", page: "/_Host");
                }

                endpoints.MapRemoteWorkersWebSocket();

                var jobsManager = app.ApplicationServices.GetRequiredService<JobsManager>();

                endpoints.MapPostJob("pricing", async input =>
                {
                    using var stream = input.PipeReader.AsStream();
                    var requestData = await ReadStreamIntoMemorySafelyAsync(stream,
                                                                            input.ContentLength,
                                                                            16 * 1024 * 1024,
                                                                            8192,
                                                                            input.CancellationToken);

                    var request = new Payload(input.ContentType ?? string.Empty, requestData);

                    var promise = input.Storage.CreatePromise(request);

                    jobsManager.PushJobAndOwnCancellation(input.JobQueueKeyRequired,
                        static w => w.Promise ?? throw new ArgumentNullException(),
                        new PromisedWork(request) { InitialWait = 100000, Promise = promise });

                    return promise.Id;
                });

                endpoints.MapPostJob("multi", input => PriceMultipleAsync(jobsManager, input));

                endpoints.MapPostJob("multi2", input => PriceMultipleAsync(jobsManager, input))
                         .RequireAuthorization();


                endpoints.MapGetPromiseById();
                endpoints.MapPostPromiseById();
                endpoints.MapGetPromiseByPath();

                endpoints.MapAuthTokenRetrieval()
                         .RequireAuthorization("basic");

                endpoints.MapAuthCookieRetrieval()
                         .RequireAuthorization("basic");
            });
        }

        private static async ValueTask<PromiseId> PriceMultipleAsync(JobsManager jobScheduling, PromiseRequest input)
        {
            // Cannot pass the stream directly to JsonSerializer.DeserializeAsync,
            // because we also need to retain the inputs to store in the promise.
            // But we might decide that is unnecessary.
            using var stream = input.PipeReader.AsStream();
            var requestData = await ReadStreamIntoMemorySafelyAsync(stream,
                                                                    input.ContentLength,
                                                                    16 * 1024 * 1024,
                                                                    8192,
                                                                    input.CancellationToken);

            var items = JsonSerializer.Deserialize<IEnumerable<MockPricingInput>>(
                new MemoryReadingStream(requestData));

            if (items == null)
                throw new InvalidDataException("Received a null list. ");

            var request = new Payload(input.ContentType ?? string.Empty, requestData);
            var promise = input.Storage.CreatePromise(request);

            var microJobs = items.Select((MockPricingInput item) =>
            {
                var g = new Random(79);
                var b = new ArrayBufferWriter<byte>();
                JsonSerializer.Serialize(new Utf8JsonWriter(b), item);
                var d = new Payload("application/json", b.WrittenMemory);
                var p = input.Storage.CreatePromise(d);
                PromiseRetriever r = static w => w.Promise!;
                return (r, new PromisedWork(d) { InitialWait = g.Next(200, 7000), Promise = p });
            });

            jobScheduling.PushMacroJobAndOwnCancellation(
                input.JobQueueKeyRequired,
                static w => w.Promise! ?? throw new ArgumentNullException(), 
                new PromisedWork(request) { Promise = promise }, 
                _ => new PromiseList(input.Storage),
                AsAsyncEnumerable(microJobs));

            return promise.Id;
        }

        private static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(IEnumerable<T> sequence)
        {
            foreach (var item in sequence)
                yield return item;
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
