using Microsoft.AspNetCore.Components;
using System;
using System.Linq;
using Hearty.Server.WebUi.Infrastructure;
using System.Collections.Generic;

namespace Hearty.Server.WebUi;

/// <summary>
/// Shows a column inside a grid with arbitrarily customizable content.
/// </summary>
/// <typeparam name="TGridItem">
/// The type of item to be displayed in each row of the grid.
/// </typeparam>
public sealed class TemplateColumn<TGridItem> : ColumnDefinition<TGridItem>
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

    /// <inheritdoc />
    public override bool CanSort => SortBy is not null;

    /// <inheritdoc />
    public override IEnumerable<TGridItem> GetSortedItems(IEnumerable<TGridItem> source, bool ascending)
    {
        if (SortBy is null)
            return source;

        var queryableSource = source.AsQueryable();
        return SortBy(queryableSource).Apply(queryableSource, ascending);
    }
}
