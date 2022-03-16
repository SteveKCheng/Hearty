using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Hearty.Server.WebUi.Infrastructure;

/// <summary>
/// Wraps a <see cref="RenderFragment" /> in a component to
/// avoid repeatedly rendering it.
/// </summary>
public class Defer : ComponentBase
{
    /// <summary>
    /// What to display in this component.
    /// </summary>
    [Parameter] 
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddContent(0, ChildContent);
    }
}
