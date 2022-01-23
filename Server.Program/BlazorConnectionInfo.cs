using Microsoft.AspNetCore.Http;

namespace JobBank.Server.Program
{
    /// <summary>
    /// Information established on a client's connection to Blazor
    /// that can be cascaded to Blazor components.
    /// </summary>
    public class BlazorConnectionInfo
    {
        public string? UserAgent { get; set; }
        public string? IpAddress { get; set; }

        public BlazorConnectionInfo()
        {
        }

        public BlazorConnectionInfo(HttpContext httpContext)
        {
            UserAgent = httpContext.Request.Headers["User-Agent"];
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        }
    }
}
