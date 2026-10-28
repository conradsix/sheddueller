namespace Sheddueller.Dashboard.Components.Ui;

/// <summary>
/// One option in a <see cref="Tabs{TValue}" /> list.
/// </summary>
/// <param name="Value">The value selected by this tab.</param>
/// <param name="Label">Visible label.</param>
/// <param name="Count">Optional count shown after the label.</param>
/// <param name="Attention">Highlights the count, for example failed jobs.</param>
/// <param name="Href">Renders the tab as a link, so the selection lives in the URL.</param>
public sealed record TabItem<TValue>(
    TValue Value,
    string Label,
    long? Count = null,
    bool Attention = false,
    string? Href = null);
