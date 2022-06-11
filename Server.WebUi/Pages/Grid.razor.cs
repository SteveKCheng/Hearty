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
    // Cannot set this constraint because of a bug in Razor's source generator
    // https://github.com/dotnet/aspnetcore/issues/38041
    /* where TGridItem : notnull */
{
    /// <summary>
    /// The sequence of rows to be displayed by this grid.
    /// </summary>
    /// <remarks>
    /// If the sequence implements <see cref="IQueryable{T}" />,
    /// sorting and selection may be delegated to the data source,
    /// instead of being evaluated "immediately".
    /// </remarks>
    [Parameter, EditorRequired] 
    public IEnumerable<TGridItem>? Items { get; set; }

    /// <summary>
    /// Holds the column definitions.
    /// </summary>
    /// <remarks>
    /// This content is not rendered but lets the columns of the grid
    /// be defined inside the "Grid" element in Blazor syntax.
    /// </remarks>
    [Parameter] 
    public RenderFragment? ChildContent { get; set; }
    
    /// <summary>
    /// Whether the grid is to be virtualized, i.e. the rows of the
    /// grid are to be rendered to HTML only when visible in the Web browser.
    /// </summary>
    [Parameter] 
    public bool Virtualize { get; set; }

    /// <summary>
    /// If true, allow the user to change the width of columns
    /// by dragging the mouse.
    /// </summary>
    [Parameter] 
    public bool ResizableColumns { get; set; }

    /// <summary>
    /// The fixed height of each row when the grid is to be virtualized,
    /// in CSS pixels.
    /// </summary>
    [Parameter] 
    public float RowHeight { get; set; } = 50;

    /// <summary>
    /// Derives the key on each row to enable DOM differencing by Blazor.
    /// </summary>
    /// <remarks>
    /// The default is to preserve row identity by the object reference.
    /// </remarks>
    [Parameter] public Func<TGridItem, object> ItemKey { get; set; } = x => x;

    /// <summary>
    /// Manages the virtualization of the display of rows.
    /// Initialized from Razor syntax.
    /// </summary>
    private Virtualize<KeyValuePair<int, TGridItem>>? _virtualizeComponent;

    /// <summary>
    /// The columns to show in this grid, in sequence.
    /// </summary>
    private readonly List<ColumnDefinition<TGridItem>> _columns = new();

    private ColumnDefinition<TGridItem>? _sortByColumn;
    private ColumnDefinition<TGridItem>? _displayOptionsForColumn;
    private bool _checkColumnOptionsPosition;
    private bool _sortByAscending;
    private IEnumerable<TGridItem>? _previousItems;
    private int _rowCount;
    private IJSObjectReference? _jsModule;
    private IJSObjectReference? _jsEventDisposable;
    private ElementReference _tableReference;
    private bool _hasPrologueRow;

    private IEnumerable<TGridItem>? GetSortedItems()
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
            string packageName = ContainingPackageAttribute.Instance?.Name ?? string.Empty;

            _jsModule = await JS.InvokeAsync<IJSObjectReference>(
                                "import", 
                                $"../_content/{packageName}{(packageName != "" ? "/" : "")}Pages/Grid.razor.js");

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

    private string AriaSortValue(ColumnDefinition<TGridItem> column)
        => _sortByColumn == column
            ? (_sortByAscending ? "ascending" : "descending")
            : "none";

    private string? ColumnHeaderClass(ColumnDefinition<TGridItem> column)
        => _sortByColumn == column
            ? $"{Grid<TGridItem>.GetCssColumnClass(column)} {(_sortByAscending ? "sorted-asc" : "sorted-desc")}"
            : Grid<TGridItem>.GetCssColumnClass(column);

    private static string? GetCssColumnClass(ColumnDefinition<TGridItem> column)
    {
        return column.Align switch
        {
            Align.Center => $"grid-col-center {column.Class}",
            Align.Right => $"grid-col-right {column.Class}",
            _ => column.Class,
        };
    }

    private async Task OnHeaderClicked(ColumnDefinition<TGridItem> column)
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

    private void OnColumnOptionsButtonClicked(ColumnDefinition<TGridItem> column)
    {
        _displayOptionsForColumn = column;
        _checkColumnOptionsPosition = true;
    }

    /// <summary>
    /// Called from Razor syntax to render the contents of the header
    /// row of the grid.
    /// </summary>
    private void RenderColumnHeaders(RenderTreeBuilder builder)
    {
        foreach (var col in _columns)
            RenderColumnHeader(builder, col);
    }

    /// <summary>
    /// Called from Razor syntax to render the contents of the prologue
    /// row of the grid.
    /// </summary>
    private void RenderColumnPrologues(RenderTreeBuilder builder)
    {
        foreach (var col in _columns)
            RenderColumnPrologue(builder, col);
    }

    /// <summary>
    /// Called from Razor syntax to render all the rows of the grid
    /// when virtualization is disabled.
    /// </summary>
    private void RenderRows(RenderTreeBuilder builder)
    {
        var rowIndex = 2; // aria-rowindex is 1-based, plus the first row is the header
        foreach (var item in GetSortedItems() ?? Enumerable.Empty<TGridItem>())
            RenderRow(builder, rowIndex++, item);
    }

    /// <summary>
    /// Reports rows for display with virtualization, 
    /// i.e. skipping rendering of rows that are not visible to the client.
    /// </summary>
    private async ValueTask<ItemsProviderResult<KeyValuePair<int, TGridItem>>> 
        ProvideVirtualizedItems(ItemsProviderRequest request)
    {
        if (Items is null)
            return new(Enumerable.Empty<KeyValuePair<int, TGridItem>>(), 0);

        // Debounce the requests. This eliminates a lot of redundant queries at the cost of slight lag after interactions.
        // If you wanted, you could try to make it only debounce on the 2nd-and-later request within a cluster.
        await Task.Delay(20);
        if (request.CancellationToken.IsCancellationRequested)
            return default;

        IEnumerable<TGridItem> records;

        var source = GetSortedItems();
        if (source is IQueryable<TGridItem> queryableSource)
        {
            // Let the data source do the selection if possible
            records = queryableSource.Skip(request.StartIndex)
                                     .Take(request.Count)
                                     .AsEnumerable();
        }
        else
        {
            records = source!.Skip(request.StartIndex)
                             .Take(request.Count);
        }

        var items = records.Select((x, i) => KeyValuePair.Create(i + request.StartIndex + 2, x));
        return new(items, totalItemCount: Items.Count());
    }

    void IGrid<TGridItem>.RegisterColumn(ColumnDefinition<TGridItem> column)
    {
        _columns.Add(column);
        _hasPrologueRow |= (column.PrologueContent is not null);
    }
}
