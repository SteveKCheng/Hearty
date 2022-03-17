using Hearty.Server.WebUi.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hearty.Server.WebUi.Pages;

/// <summary>
/// A basic grid control for Blazor with virtualization.
/// </summary>
/// <typeparam name="TGridItem">
/// The type of item to be displayed in each row of the grid.
/// </typeparam>
[CascadingTypeParameter(nameof(TGridItem))]
public sealed partial class Grid<TGridItem> : IGrid<TGridItem>, IAsyncDisposable
    where TGridItem : notnull
{
    /// <summary>
    /// The rows to be displayed by this grid.
    /// </summary>
    [Parameter, EditorRequired] 
    public IQueryable<TGridItem>? Items { get; set; }

    /// <summary>
    /// Holds the column definitions.
    /// </summary>
    /// <remarks>
    /// This content is not rendered but lets the columns of the grid
    /// be defined inside the "Grid" element in Blazor syntax.
    /// </remarks>
    [Parameter] 
    public RenderFragment? ChildContent { get; set; }
    
    [Parameter] 
    public bool Virtualize { get; set; }

    /// <summary>
    /// If true, allow the user to change the width of columns
    /// by dragging the mouse.
    /// </summary>
    [Parameter] 
    public bool ResizableColumns { get; set; }

    [Parameter] 
    public float ItemSize { get; set; } = 50;

    /// <summary>
    /// Derives the key on each row to enable DOM differencing by Blazor.
    /// </summary>
    /// <remarks>
    /// The default is to preserve row identity by the object reference.
    /// </remarks>
    [Parameter] public Func<TGridItem, object> ItemKey { get; set; } = x => x;

    private Virtualize<(int, TGridItem)>? _virtualizeComponent;

    /// <summary>
    /// The columns to show in this grid, in sequence.
    /// </summary>
    private readonly List<ColumnBase<TGridItem>> _columns = new();

    private ColumnBase<TGridItem>? _sortByColumn;
    private ColumnBase<TGridItem>? _displayOptionsForColumn;
    private bool _checkColumnOptionsPosition;
    private bool _sortByAscending;
    private IQueryable<TGridItem>? _previousItems;
    private int _rowCount;
    private IJSObjectReference? _jsModule;
    private IJSObjectReference? _jsEventDisposable;
    private ElementReference _tableReference;

    private const string PackageName = "Hearty.Server.WebUi";

    private IQueryable<TGridItem>? SortedItems
        => (_sortByColumn is null || Items is null) 
            ? Items 
            : _sortByColumn.GetSortedItems(Items, _sortByAscending);

    /// <summary>
    /// Constructor.
    /// </summary>
    public Grid()
    {
    }

    /// <inheritdoc cref="IAsyncDisposable.DisposeAsync" />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_jsEventDisposable is not null)
            {
                await _jsEventDisposable.InvokeVoidAsync("stop");
                await _jsEventDisposable.DisposeAsync();
            }
            if (_jsModule is not null)
            {
                await _jsModule.DisposeAsync();
            }
        }
        catch
        {
        }
    }

    void CloseColumnOptions()
    {
        _displayOptionsForColumn = null;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>(
                                "import", 
                                $"../_content/{PackageName}/Pages/Grid.razor.js");

            _jsEventDisposable = await _jsModule.InvokeAsync<IJSObjectReference>(
                                    "init", 
                                    _tableReference);
        }

        if (_checkColumnOptionsPosition && _displayOptionsForColumn is not null)
        {
            _checkColumnOptionsPosition = false;
            _ = _jsModule?.InvokeVoidAsync("checkColumnOptionsPosition", _tableReference);
        }
    }

    /// <inheritdoc />
    public override Task SetParametersAsync(ParameterView parameters)
    {
        _previousItems = Items;
        return base.SetParametersAsync(parameters);
    }

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        // The extra 1 is the header row. This matches the default behavior.
        _rowCount = (Items?.Count() ?? 0) + 1; 

        return _virtualizeComponent is not null && Items != _previousItems
                ? _virtualizeComponent.RefreshDataAsync()
                : Task.CompletedTask;
    }

    private void RenderRows(RenderTreeBuilder builder)
    {
        var rowIndex = 2; // aria-rowindex is 1-based, plus the first row is the header
        foreach (var item in SortedItems ?? Enumerable.Empty<TGridItem>())
            RenderRow(builder, rowIndex++, item);
    }

    private string AriaSortValue(ColumnBase<TGridItem> column)
        => _sortByColumn == column
            ? (_sortByAscending ? "ascending" : "descending")
            : "none";

    private string? ColumnHeaderClass(ColumnBase<TGridItem> column)
        => _sortByColumn == column
            ? $"{Grid<TGridItem>.GetCssColumnClass(column)} {(_sortByAscending ? "sorted-asc" : "sorted-desc")}"
            : Grid<TGridItem>.GetCssColumnClass(column);

    private static string? GetCssColumnClass(ColumnBase<TGridItem> column)
    {
        return column.Align switch
        {
            Align.Center => $"grid-col-center {column.Class}",
            Align.Right => $"grid-col-right {column.Class}",
            _ => column.Class,
        };
    }

    private async Task OnHeaderClicked(ColumnBase<TGridItem> column)
    {
        if (column.CanSort)
        {
            if (_sortByColumn == column)
            {
                _sortByAscending = !_sortByAscending;
            }
            else
            {
                _sortByAscending = true;
                _sortByColumn = column;
            }

            if (_virtualizeComponent is not null)
                await _virtualizeComponent.RefreshDataAsync();
        }
    }

    private void OnColumnOptionsButtonClicked(ColumnBase<TGridItem> column)
    {
        _displayOptionsForColumn = column;
        _checkColumnOptionsPosition = true;
    }

    /// <summary>
    /// Reports rows for display with virtualization, 
    /// i.e. skipping rendering of rows that are not visible to the client.
    /// </summary>
    private async ValueTask<ItemsProviderResult<(int, TGridItem)>> 
        ProvideVirtualizedItems(ItemsProviderRequest request)
    {
        if (Items is null)
            return new ItemsProviderResult<(int, TGridItem)>(Enumerable.Empty<(int, TGridItem)>(), 0);

        // Debounce the requests. This eliminates a lot of redundant queries at the cost of slight lag after interactions.
        // If you wanted, you could try to make it only debounce on the 2nd-and-later request within a cluster.
        await Task.Delay(20);
        if (request.CancellationToken.IsCancellationRequested)
            return default;

        var records = SortedItems!.Skip(request.StartIndex)
                                  .Take(request.Count)
                                  .AsEnumerable();

        var result = new ItemsProviderResult<(int, TGridItem)>(
            items: records.Select((x, i) => (i + request.StartIndex + 2, x)),
            totalItemCount: Items.Count());

        return result;
    }

    void IGrid<TGridItem>.RegisterColumn(ColumnBase<TGridItem> column)
        => _columns.Add(column);
}
