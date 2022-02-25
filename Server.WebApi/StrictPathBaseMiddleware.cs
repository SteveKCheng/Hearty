using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hearty.Server.WebApi
{
    /// <summary>
    /// ASP.NET Core middleware that expects a path base (prefix) 
    /// in the URL of an incoming request, and strips it off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This middleware differs from <see cref="UsePathBaseMiddleware" />
    /// in that the path base is treated as required, not optional.
    /// It is important for ensuring canonical URLs.  It is
    /// also critical for testing endpoints to work with an arbitrary
    /// path base, such that they do not ever return responses or
    /// content that accidentally omit it.
    /// </para>
    /// <para>
    /// When the incoming URL does not start with the designated
    /// path base, a separate failure path is invoked, which might
    /// result in a "not found" response, a default page, 
    /// or a redirection to the correct URL.
    /// </para>
    /// </remarks>
    public class StrictPathBaseMiddleware
    {
        private readonly RequestDelegate _onMatched;
        private readonly RequestDelegate _onUnmatched;
        private readonly PathString _pathBase;

        /// <summary>
        /// Construct this strict path-base-matching middleware.
        /// </summary>
        /// <param name="onMatched">
        /// The rest of the ASP.NET Core pipeline to execute when
        /// the incoming URL starts with the path base.
        /// The path base is stripped off of <see cref="HttpRequest.Path" />
        /// and appended onto <see cref="HttpRequest.PathBase" />
        /// when this delegate is executed.
        /// </param>
        /// <param name="onUnmatched">
        /// The ASP.NET Core pipeline to execute when the incoming
        /// URL does not start with the path base.
        /// </param>
        /// <param name="pathBase">
        /// The desired prefix to match in the incoming URL.
        /// </param>
        public StrictPathBaseMiddleware(RequestDelegate onMatched,
                                        RequestDelegate onUnmatched,
                                        PathString pathBase)
        {
            _onMatched = onMatched;
            _onUnmatched = onUnmatched;
            _pathBase = pathBase;
        }

        /// <summary>
        /// Executes the middleware.
        /// </summary>
        /// <param name="context">The context for the current request.</param>
        /// <returns>A task that represents the execution of this middleware. </returns>
        public Task Invoke(HttpContext context)
        {
            if (context.Request.Path.StartsWithSegments(_pathBase,
                                                        out var matchedPath,
                                                        out var remainingPath))
                return InvokeImplAsync(context, matchedPath, remainingPath);
            else
                return _onUnmatched(context);
        }

        private async Task InvokeImplAsync(HttpContext context,
                                           string matchedPath,
                                           string remainingPath)
        {
            var origPath = context.Request.Path;
            var origPathBase = context.Request.PathBase;

            context.Request.Path = remainingPath;
            context.Request.PathBase = origPathBase.Add(matchedPath);

            try
            {
                await _onMatched(context);
            }
            finally
            {
                context.Request.Path = origPath;
                context.Request.PathBase = origPathBase;
            }
        }
    }

    /// <summary>
    /// Extension methods to build strict path-base matching
    /// into the pipeline of an ASP.NET Core application.
    /// </summary>
    public static class UseStrictPathBaseExtensions
    {
        /// <summary>
        /// Expect and strip off a path base in the URL of incoming 
        /// HTTP requests, or return a "not found" response otherwise.
        /// </summary>
        /// <param name="app">The ASP.NET Core middleware pipeline. </param>
        /// <param name="pathBase">The path base to expect. </param>
        /// <returns>
        /// The same object as <paramref name="app" />, for chaining.
        /// </returns>
        public static IApplicationBuilder
            UseStrictPathBaseOrNotFound(this IApplicationBuilder app, 
                                        PathString pathBase)
        {
            return UseStrictPathBase(app, pathBase,
                httpContext =>
                {
                    httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                });
        }

        /// <summary>
        /// Expect and strip off a path base in the URL of incoming 
        /// HTTP requests, or redirect to the path base.
        /// </summary>
        /// <remarks>
        /// Redirection only occurs for the top-level path.
        /// Sub-paths still return a "not found" response.
        /// </remarks>
        /// <param name="app">The ASP.NET Core middleware pipeline. </param>
        /// <param name="pathBase">The path base to expect. </param>
        /// <returns>
        /// The same object as <paramref name="app" />, for chaining.
        /// </returns>
        public static IApplicationBuilder
            UseStrictPathBaseOrRedirect(this IApplicationBuilder app,
                                        PathString pathBase)
        {
            return UseStrictPathBaseOrRedirect(app, pathBase, pathBase);
        }

        /// <summary>
        /// Expect and strip off a path base in the URL of incoming 
        /// HTTP requests, or redirect.
        /// </summary>
        /// <remarks>
        /// Redirection only occurs for the top-level path.
        /// Sub-paths still return a "not found" response.
        /// </remarks>
        /// <param name="app">The ASP.NET Core middleware pipeline. </param>
        /// <param name="pathBase">The path base to expect. </param>
        /// <param name="redirectPath">The path to redirect to, relative
        /// to the path base applied before this middleware (typically
        /// empty).
        /// </param>
        /// <returns>
        /// The same object as <paramref name="app" />, for chaining.
        /// </returns>
        public static IApplicationBuilder
            UseStrictPathBaseOrRedirect(this IApplicationBuilder app,
                                        PathString pathBase,
                                        PathString redirectPath)
        {
            return UseStrictPathBase(app, pathBase,
                httpContext =>
                {
                    var request = httpContext.Request;
                    var response = httpContext.Response;
                    var path = request.Path;
                    if (!path.HasValue || path.Equals("/", StringComparison.Ordinal))
                    {
                        response.StatusCode = StatusCodes.Status301MovedPermanently;
                        response.Headers.Location = request.PathBase.Add(redirectPath).ToString();
                    }
                    else
                    {
                        response.StatusCode = StatusCodes.Status404NotFound;
                    }

                    return Task.CompletedTask;
                });
        }

        /// <summary>
        /// Expect and strip off a path base in the URL of incoming 
        /// HTTP requests, or execute a failure path.
        /// </summary>
        /// <param name="app">The ASP.NET Core middleware pipeline. </param>
        /// <param name="pathBase">The path base to expect. </param>
        /// <param name="onUnmatched">The action to take on failing
        /// to match the path base. </param>
        /// <returns>
        /// The same object as <paramref name="app" />, for chaining.
        /// </returns>
        public static IApplicationBuilder 
            UseStrictPathBase(this IApplicationBuilder app, 
                              PathString pathBase, 
                              RequestDelegate onUnmatched)
        {
            // Strip trailing slashes
            pathBase = pathBase.Value?.TrimEnd('/');
            if (!pathBase.HasValue)
                return app;

            return app.UseMiddleware<StrictPathBaseMiddleware>(onUnmatched, pathBase);
        }

        /// <summary>
        /// Expect and strip off a path base in the URL of incoming 
        /// HTTP requests, or execute a forked pipeline.
        /// </summary>
        /// <param name="app">The ASP.NET Core middleware pipeline. </param>
        /// <param name="pathBase">The path base to expect. </param>
        /// <param name="onUnmatched">Constructs the sub-pipeline 
        /// to execute on failure to match the path base. </param>
        /// <returns>
        /// The same object as <paramref name="app" />, for chaining.
        /// </returns>
        public static IApplicationBuilder
            UseStrictPathBaseWithFork(this IApplicationBuilder app, 
                                      PathString pathBase,
                                      Action<IApplicationBuilder> onUnmatched)
        {
            var forkedApp = app.ApplicationServices
                               .GetRequiredService<IApplicationBuilderFactory>()
                               .CreateBuilder(app.ServerFeatures);

            onUnmatched(forkedApp);
            return UseStrictPathBase(app, pathBase, forkedApp.Build());
        }
    }
}
