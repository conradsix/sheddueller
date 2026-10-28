namespace Sheddueller.Dashboard.Components.Ui;

/// <summary>
/// One choice in a <see cref="FacetedFilter{TValue}" />.
/// </summary>
/// <param name="Value">The value the option selects.</param>
/// <param name="Label">Visible label.</param>
/// <param name="DotClass">Optional class for a coloured dot before the label, e.g. <c>sd-state-dot sd-state-dot--failed</c>.</param>
public sealed record FacetOption<TValue>(
    TValue Value,
    string Label,
    string? DotClass = null);
