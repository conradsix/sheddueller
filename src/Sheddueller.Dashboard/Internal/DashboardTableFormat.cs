namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

/// <summary>
/// Classes and placeholder sizes shared by the list pages' tables.
/// </summary>
internal static class DashboardTableFormat
{
    /// <summary>
    /// The table card's classes: the shared card plus the page's own, dimmed while a new query replaces the rows.
    /// </summary>
    public static string CardClass(
        string pageCardClass,
        bool isReloading)
      => isReloading
        ? string.Concat("sd-table-card ", pageCardClass, " sd-table-card--loading")
        : string.Concat("sd-table-card ", pageCardClass);

    /// <summary>
    /// Width of a loading placeholder cell, varied by row and column so the placeholders don't line up in a grid.
    /// </summary>
    public static string SkeletonWidth(
        int row,
        int column)
      => string.Create(CultureInfo.InvariantCulture, $"{45 + (((row * 7) + (column * 13)) % 45)}%");
}
