using System;
using Microsoft.AspNetCore.Components;
using System.Linq;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Hearty.Server.WebUi;

/// <summary>
/// Shows a column inside a grid where a property of the item is displayed.
/// </summary>
/// <typeparam name="TGridItem">
/// The type of item to be displayed in each row of the grid.
/// </typeparam>
/// <typeparam name="TProp">
/// The type of value returned by the property.
/// </typeparam>
public sealed class PropertyColumn<TGridItem, TProp> : ColumnDefinition<TGridItem>
{
    private Func<TGridItem, TProp>? _property;
    private Expression<Func<TGridItem, TProp>>? _expression;
    private bool _propertyNeedsRendering;

    /// <summary>
    /// Projects an item (row) into a value to display in this column.
    /// </summary>
    /// <remarks>
    /// This function is called for rendering the item after it
    /// has been received (possibly from a database).
    /// If this member is null but <see cref="Expression" />
    /// is not, then the latter is compiled into a function.
    /// Specify both properties to avoid run-time compilation
    /// of LINQ expressions.
    /// </remarks>
    [Parameter]
    public Func<TGridItem, TProp>? Property
    {
        get => _property;
        set
        {
            _propertyNeedsRendering = true;
            _property = value;
        }
    }

    /// <summary>
    /// Projects an item (row) into a value to display in this column,
    /// as a LINQ expression.
    /// </summary>
    /// <remarks>
    /// This LINQ expression can be incorporated as part of a
    /// query, to let the LINQ provider (database) do filtering 
    /// and sorting.
    /// </remarks>
    [Parameter]
    public Expression<Func<TGridItem, TProp>>? Expression
    {
        get => _expression;
        set
        {
            _propertyNeedsRendering = true;
            _expression = value;
        }
    }

    /// <summary>
    /// The format specification to convert <typeparamref name="TProp" /> 
    /// for displaying in a grid cell.
    /// </summary>
    /// <remarks>
    /// This format string is passed to 
    /// <see cref="IFormattable.ToString(string?, IFormatProvider?)" />.
    /// </remarks>
    [Parameter] 
    public string? Format { get; set; }
    
    [Parameter] 
    public EventCallback<TGridItem> OnCellClicked { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!_propertyNeedsRendering)
            return;

        if (_property is null)
        {
            if (_expression is null)
            {
                _propertyNeedsRendering = false;
                CellContent = EmptyChildContent;
                return;
            }

            _property = _expression.Compile();
        }

        Func<TGridItem, string> cellTextFunc;
        if (!string.IsNullOrEmpty(Format))
        {
            Type propertyType = Nullable.GetUnderlyingType(typeof(TProp)) ?? typeof(TProp);

            if (!typeof(IFormattable).IsAssignableFrom(propertyType))
            {
                throw new InvalidOperationException(
                    $"A '{nameof(Format)}' parameter was supplied, but the type '{typeof(TProp)}' does not implement '{typeof(IFormattable)}'.");
            }

            cellTextFunc = item => ((IFormattable?)_property!.Invoke(item))?.ToString(Format, null) ?? string.Empty;
        }
        else
        {
            cellTextFunc = item => _property!.Invoke(item)?.ToString() ?? string.Empty;
        }

        if (OnCellClicked.HasDelegate)
        {
            CellContent = item => builder =>
            {
                builder.OpenElement(0, "button");
                builder.AddAttribute(1, "onclick", () => OnCellClicked.InvokeAsync(item));
                builder.AddContent(2, cellTextFunc(item));
                builder.CloseElement();
            };
        }
        else
        {
            CellContent = item => builder => builder.AddContent(0, cellTextFunc(item));
        }

        _propertyNeedsRendering = false;
    }

    /// <inheritdoc />
    public override bool CanSort => true;

    /// <inheritdoc />
    public override IEnumerable<TGridItem> GetSortedItems(IEnumerable<TGridItem> source, bool ascending)
    {
        if (_expression is { }pe && source is IQueryable<TGridItem> queryable)
        {
            return ascending ? queryable.OrderBy(pe)
                             : queryable.OrderByDescending(pe);
        }
        else if (_property is { }p)
        {
            return ascending ? source.OrderBy(p)
                             : source.OrderByDescending(p);
        }
        else
        {
            return source;
        }
    }
}
