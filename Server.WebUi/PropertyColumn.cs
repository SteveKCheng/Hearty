using System;
using Microsoft.AspNetCore.Components;
using System.Linq.Expressions;
using System.Linq;

namespace Hearty.Server.WebUi;

/// <summary>
/// Shows a column in <see cref="Hearty.Server.WebUi.Pages.Grid{TGridItem}" />
/// where a property of the item is displayed.
/// </summary>
/// <typeparam name="TGridItem">
/// The type of item to be displayed in each row of the grid.
/// </typeparam>
/// <typeparam name="TProp">
/// The type of value returned by the property.
/// </typeparam>
public class PropertyColumn<TGridItem, TProp> : ColumnBase<TGridItem>
{
    private Expression<Func<TGridItem, TProp>>? _cachedProperty;
    private Func<TGridItem, TProp>? _compiledPropertyExpression;

    [Parameter, EditorRequired] 
    public Expression<Func<TGridItem, TProp>> Property { get; set; } = default!;
    
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
        if (_cachedProperty != Property)
        {
            _cachedProperty = Property;
            _compiledPropertyExpression = Property.Compile();

            Func<TGridItem, string?> cellTextFunc;
            if (!string.IsNullOrEmpty(Format))
            {
                Type propertyType = Nullable.GetUnderlyingType(typeof(TProp)) ?? typeof(TProp);

                if (!typeof(IFormattable).IsAssignableFrom(propertyType))
                {
                    throw new InvalidOperationException(
                        $"A '{nameof(Format)}' parameter was supplied, but the type '{typeof(TProp)}' does not implement '{typeof(IFormattable)}'.");
                }

                cellTextFunc = item => ((IFormattable?)_compiledPropertyExpression!(item))?.ToString(Format, null);
            }
            else
            {
                cellTextFunc = item => _compiledPropertyExpression!(item)?.ToString();
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

        if (Title is null && _cachedProperty.Body is MemberExpression memberExpression)
            Title = memberExpression.Member.Name;
    }

    internal override bool CanSort => true;

    internal override IQueryable<TGridItem> GetSortedItems(IQueryable<TGridItem> source, bool ascending)
        => ascending ? source.OrderBy(Property) 
                     : source.OrderByDescending(Property);
}
