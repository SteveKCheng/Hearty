using System;
using Microsoft.AspNetCore.Components;
using System.Linq;
using System.Collections.Generic;

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
    private object? _cachedProperty;

    /// <summary>
    /// Projects an item (row) into a value to display in this column.
    /// </summary>
    [Parameter, EditorRequired] 
    public Func<TGridItem, TProp> Property { get; set; } = default!;
    
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
        if (!object.ReferenceEquals(_cachedProperty, Property))
        {
            _cachedProperty = Property;

            Func<TGridItem, string> cellTextFunc;
            if (!string.IsNullOrEmpty(Format))
            {
                Type propertyType = Nullable.GetUnderlyingType(typeof(TProp)) ?? typeof(TProp);

                if (!typeof(IFormattable).IsAssignableFrom(propertyType))
                {
                    throw new InvalidOperationException(
                        $"A '{nameof(Format)}' parameter was supplied, but the type '{typeof(TProp)}' does not implement '{typeof(IFormattable)}'.");
                }

                cellTextFunc = item => ((IFormattable?)Property.Invoke(item))?.ToString(Format, null) ?? string.Empty;
            }
            else
            {
                cellTextFunc = item => Property.Invoke(item)?.ToString() ?? string.Empty;
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
        }
    }

    /// <inheritdoc />
    public override bool CanSort => true;

    /// <inheritdoc />
    public override IEnumerable<TGridItem> GetSortedItems(IEnumerable<TGridItem> source, bool ascending)
    {
        return ascending ? source.OrderBy(Property)
                         : source.OrderByDescending(Property);
    }
}
