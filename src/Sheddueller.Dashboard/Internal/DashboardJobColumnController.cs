namespace Sheddueller.Dashboard.Internal;

internal sealed class DashboardJobColumnController
{
    public List<ShedduellerDashboardJobColumn> Columns { get; } = [];

    public void Replace(IReadOnlyList<ShedduellerDashboardJobColumn> columns)
    {
        this.Columns.Clear();
        this.Columns.AddRange(columns);
    }

    public bool ResizeByKeyboard(int index, string key, bool shift)
    {
        if (index < 0 || index >= this.Columns.Count)
        {
            return false;
        }

        var currentWidth = DashboardJobColumnWidths.Get(this.Columns[index]);
        var step = shift ? 32 : 8;
        var nextWidth = key switch
        {
            "ArrowLeft" => currentWidth - step,
            "ArrowRight" => currentWidth + step,
            "Home" => DashboardJobColumnWidths.MinimumOverride,
            "End" => DashboardJobColumnWidths.MaximumOverride,
            _ => currentWidth,
        };

        return nextWidth != currentWidth
          && DashboardJobColumnWidths.Set(
              this.Columns, index,
              Math.Clamp(nextWidth, DashboardJobColumnWidths.MinimumOverride, DashboardJobColumnWidths.MaximumOverride));
    }

    public bool ApplyMeasuredWidths(IReadOnlyList<int> widths)
    {
        var changed = false;
        for (var index = 0; index < Math.Min(this.Columns.Count, widths.Count); index++)
        {
            changed |= DashboardJobColumnWidths.Set(this.Columns, index, widths[index]);
        }

        return changed;
    }
}
