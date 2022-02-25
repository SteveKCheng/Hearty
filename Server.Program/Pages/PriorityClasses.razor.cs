using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hearty.Server.Program.Pages
{
    public partial class PriorityClasses : IDisposable
    {
        private bool _isDisposed;

        protected override void OnInitialized()
        {
            _timeoutProvider.Register(TimeoutBucket.After1Second,
                (_, _) =>
                {
                    // TimeoutProvider does not provide cancellation, so
                    // keep polling until the page is disposed.  False
                    // positives can occur when this check races with
                    // disposal, but they are harmless.
                    if (_isDisposed)
                        return false;

                    InvokeAsync(() =>
                    {
                        // Definitely check this flag again after we
                        // enter the page's synchronization context to
                        // avoid races, assuming disposal also happens
                        // in the same synchronization context.
                        if (!_isDisposed)
                            StateHasChanged();
                    });

                    return true;
                }, null);
        }

        public void Dispose()
        {
            _isDisposed = true;
        }
    }
}
