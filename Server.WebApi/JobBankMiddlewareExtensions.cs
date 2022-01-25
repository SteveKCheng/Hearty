using Microsoft.AspNetCore.Builder;

namespace JobBank.Server.WebApi
{
    /// <summary>
    /// Provides ASP.NET Core middleware 
    /// implemented by the JobBank framework.
    /// </summary>
    public static class JobBankMiddlewareExtensions
    {
        /// <summary>
        /// Installs middleware that listens for and accepts 
        /// remote hosts that can perform work in the JobBank framework.
        /// </summary>
        /// <param name="builder">
        /// The ASP.NET Core application pipeline being built.
        /// </param>
        /// <remarks>
        /// See <see cref="RemoteWorkersMiddleware" />
        /// for the middleware requires, in how it should be
        /// positioned versus other middleware, and what services
        /// need to be injected.
        /// </remarks>
        /// <returns>
        /// Returns the same object as <paramref name="builder" />.
        /// </returns>
        public static IApplicationBuilder UseRemoteWorkers(
                this IApplicationBuilder builder)
            => builder.UseMiddleware<RemoteWorkersMiddleware>();
    }
}
