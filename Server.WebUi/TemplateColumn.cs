using Microsoft.AspNetCore.Components;
using System;
using System.Linq;
using Hearty.Server.WebUi.Infrastructure;

namespace Hearty.Server.WebUi;

/// <summary>
/// Shows a column in <see cref="Hearty.Server.WebUi.Pages.Grid{TGridItem}" />
/// with arbitrarily customizable content.
/// </summary>
/// <typeparam name="TGridItem">
/// The type of item to be displayed in each row of the grid.
/// </typeparam>
public class TemplateColumn<TGridItem> : ColumnBase<TGridItem>
{
    /// <summary>
    /// Renders the cell for this column and the given row.
    /// </summary>
    [Parameter] 
    public RenderFragment<TGridItem> ChildContent { get; set; } = EmptyChildContent;
    
    [Parameter] 
    public Func<IQueryable<TGridItem>, SortBy<TGridItem>>? SortBy { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        CellContent = ChildContent;
    }

    internal override bool CanSort => SortBy != null;

    internal override IQueryable<TGridItem> GetSortedItems(IQueryable<TGridItem> source, bool ascending)
        => SortBy == null ? source 
                          : SortBy(source).Apply(source, ascending);
}
