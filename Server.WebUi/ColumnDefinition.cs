﻿// This file and related files for "Grid" were originally
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
using Hearty.Server.WebUi.Infrastructure;
using System.Linq;
using System.Collections.Generic;

namespace Hearty.Server.WebUi;

/// <summary>
/// Lets <see cref="ColumnDefinition{TGridItem}" /> communicate
/// with the grid that owns it.
/// </summary>
/// <typeparam name="TGridItem">
/// The type of item to be displayed in each row of the grid.
/// </typeparam>
internal interface IGrid<TGridItem>
{
    /// <summary>
    /// Called by the definition of a column to register
    /// itself to the grid.
    /// </summary>
    void RegisterColumn(ColumnDefinition<TGridItem> column);
}

/// <summary>
/// Describes a column inside <see cref="IGrid{TGridItem}" />.
/// </summary>
/// <typeparam name="TGridItem">
/// The type of item to be displayed in each row of the grid.
/// </typeparam>
public abstract class ColumnDefinition<TGridItem> : ComponentBase
{
    /// <summary>
    /// A render fragment for an item that contains nothing,
    /// used as the default value of user-customizable fragments.
    /// </summary>
    protected static RenderFragment<TGridItem> 
        EmptyChildContent { get; } = _ => builder => { };

    /// <summary>
    /// Reference to the grid that owns this column.
    /// </summary>
    [CascadingParameter] 
    private IGrid<TGridItem> OwningGrid { get; set; } = default!;

    /// <summary>
    /// The title of the column in the grid, shown in a header cell.
    /// </summary>
    [Parameter] 
    public string? Title { get; set; }
    
    /// <summary>
    /// The CSS class set into each cell for this column in the grid.
    /// </summary>
    [Parameter] 
    public string? Class { get; set; }
    
    /// <summary>
    /// The horizontal alignment of the contents of this column
    /// in the grid.
    /// </summary>
    [Parameter] 
    public Align Align { get; set; }

    /// <summary>
    /// A fragment to show when the header of the column is clicked.
    /// </summary>
    /// <remarks>
    /// Such a fragment can contain user-adjustable controls 
    /// on that column.  If null, the column will not have
    /// this feature.
    /// </remarks>
    [Parameter] 
    public RenderFragment? ColumnOptions { get; set; }

    /// <summary>
    /// A fragment to show for this column on a "prologue row"
    /// situated above the header row.
    /// </summary>
    /// <remarks>
    /// This area can be used to show a
    /// text box for filtering entries that is always visible
    /// above the headers.  The prologue row is not rendered if
    /// no columns have prologue content (i.e. this property
    /// is left as null).
    /// </remarks>
    [Parameter]
    public RenderFragment? PrologueContent { get; set; }

    /// <summary>
    /// A fragment to show, inside the header for this column.
    /// </summary>
    internal RenderFragment HeaderContent { get; }

    /// <summary>
    /// Invoked to render the cell under this column
    /// for the given item (row).
    /// </summary>
    protected internal RenderFragment<TGridItem> 
        CellContent { get; protected set; } = EmptyChildContent;

    /// <summary>
    /// Constructor.
    /// </summary>
    public ColumnDefinition()
    {
        HeaderContent = __builder => __builder.AddContent(0, Title);
    }

    /// <summary>
    /// Whether the items can be sorted based on this column.
    /// </summary>
    public virtual bool CanSort => false;

    /// <summary>
    /// Get the sequence of items after sorting on this column.
    /// </summary>
    /// <param name="source">
    /// The original sequence of items.  Although this argument
    /// is typed as <see cref="IEnumerable{T}" />,
    /// if the object implements <see cref="IQueryable{T}" /> also,
    /// then this method should, if possible, defer the
    /// sorting to when the sequence is evaluated from the data source.
    /// </param>
    /// <param name="ascending">
    /// Set to true to sort in ascending order.  False to sort in
    /// descending order.
    /// </param>
    public virtual IEnumerable<TGridItem> 
        GetSortedItems(IEnumerable<TGridItem> source, bool ascending) => source;

    /// <inheritdoc />
    protected sealed override void BuildRenderTree(RenderTreeBuilder builder)
    {
        OwningGrid.RegisterColumn(this);
    }
}
