using Microsoft.AspNetCore.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Server.WebUi.Pages;

public partial class WorkersView
{
    /// <inheritdoc />
    protected override void OnInitialized()
        => StartRefreshing(TimeoutBucket.After5Seconds);

    /// <summary>
    /// If true, allow workers to be disconnected through this UI control.
    /// </summary>
    /// <remarks>
    /// Clients of this UI control may want to set this parameter 
    /// according to whether the logged in user is authorized to
    /// disconnect workers.
    /// </remarks>
    [Parameter]
    public bool AllowDisconnect { get; set; } = true;
}
