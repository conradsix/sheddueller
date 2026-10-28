namespace Sheddueller.Dashboard.Components.Ui;

/// <summary>
/// A free-text filter offered by <see cref="TextFilterMenu" />.
/// </summary>
/// <param name="Key">Identifies the field when it is applied.</param>
/// <param name="Label">Name shown in the menu, e.g. <c>Tag contains</c>.</param>
/// <param name="Placeholder">Example input.</param>
/// <param name="Value">The currently applied value, if any.</param>
public sealed record TextFilterField(
    string Key,
    string Label,
    string Placeholder,
    string? Value = null);
