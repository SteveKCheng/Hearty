using Microsoft.AspNetCore.Http;

namespace Hearty.Server.Program
{
    /// <summary>
    /// Information established on a client's connection to Blazor
    /// that can be cascaded to Blazor components.
    /// </summary>
    public class BlazorConnectionInfo
    {
        public string? UserAgent { get; set; }
        public string? IpAddress { get; set; }

        public string ServerHost { get; set; } = null!;

        public int? ServerPort { get; set; }

        public bool IsSecure { get; set; }

        public string PathBase { get; set; }

        public BlazorConnectionInfo()
        {
        }

        public BlazorConnectionInfo(HttpContext httpContext)
        {
            var host = httpContext.Request.Host;
            ServerHost = host.Host;
            ServerPort = host.Port;
            IsSecure = httpContext.Request.IsHttps;
            PathBase = httpContext.Request.PathBase;

            UserAgent = httpContext.Request.Headers["User-Agent"];
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        }
    }
}
