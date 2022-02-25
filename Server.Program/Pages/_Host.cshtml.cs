using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hearty.Server.Program.Pages
{
    public partial class HostPageModel : PageModel
    {
        private readonly IHttpContextAccessor _httpContextAccess;

        public HostPageModel(IHttpContextAccessor httpContextAccess)
        {
            _httpContextAccess = httpContextAccess;
        }

        public BlazorConnectionInfo? ConnectionInfo { get; private set; }

        public void OnGet()
        {
            var httpContext = _httpContextAccess.HttpContext!;
            ConnectionInfo = new BlazorConnectionInfo(httpContext);
        }
    }
}
