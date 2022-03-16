// This file and related files for "Grid" were originally
// written by Steve Sanderson for a demo, and came from:
// 
//   https://github.com/SteveSandersonMS/BlazeOrbital
//
// This "Grid" component is lightweight and easily
// servicible within this library.
// 
// The author of this library has considered GridBlazor, from:
//
//   https://github.com/gustavnavar/Grid.Blazor
// 
// While GridBlazor is full-featured, it is quite a large
// dependency, and, unfortunately, it essentially has no
// code documentation at the API level.  That makes it quite
// risky to use when the requirements of this library are
// rather modest.
//
// While BlazeOrbital's Grid was not documented either, 
// the code base is small enough that the author of this
// library was able to dig into it immediately and add
// his own documentation.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Hearty.Server.WebUi.Pages;
using Hearty.Server.WebUi.Infrastructure;
using System.Linq;

namespace Hearty.Server.WebUi;

/// <summary>
/// Describes a column inside <see cref="Grid{TGridItem}" />.
/// </summary>
/// <typeparam name="TGridItem">
/// The type of item to be displayed in each row of the grid.
/// </typeparam>
public abstract class ColumnBase<TGridItem> : ComponentBase
{
    private readonly static RenderFragment<TGridItem> EmptyChildContent = _ => builder => { };

    [CascadingParameter] 
    internal Grid<TGridItem>.AddColumnCallback AddColumn { get; set; } = default!;

    [Parameter] 
    public string? Title { get; set; }
    
    [Parameter] 
    public string? Class { get; set; }
    
    [Parameter] 
    public Align Align { get; set; }
    
    [Parameter] 
    public RenderFragment? ColumnOptions { get; set; }

    internal RenderFragment HeaderContent { get; }

    protected internal RenderFragment<TGridItem> CellContent { get; protected set; } = EmptyChildContent;

    public ColumnBase()
    {
        HeaderContent = __builder => __builder.AddContent(0, Title);
    }

    internal virtual bool CanSort => false;

    internal virtual IQueryable<TGridItem> GetSortedItems(IQueryable<TGridItem> source, bool ascending) => source;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        AddColumn(this);
    }
}
