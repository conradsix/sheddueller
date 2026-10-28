namespace Sheddueller.Dashboard.Internal;

internal static class DashboardJobColumnWidths
{
    public const int MinimumOverride = 48;
    public const int MaximumOverride = 800;

    public static bool IsValid(int? width)
      => width is null or (>= MinimumOverride and <= MaximumOverride);

    public static int Get(ShedduellerDashboardJobColumn column)
      => column.Width ?? Default(column.Kind);

    public static int Default(ShedduellerDashboardJobColumnKind kind)
      => kind switch
      {
          ShedduellerDashboardJobColumnKind.JobId => 64,
          ShedduellerDashboardJobColumnKind.Enqueued or ShedduellerDashboardJobColumnKind.TerminalTime => 160,
          ShedduellerDashboardJobColumnKind.State => 128,
          ShedduellerDashboardJobColumnKind.Queue => 96,
          ShedduellerDashboardJobColumnKind.Handler => 220,
          ShedduellerDashboardJobColumnKind.Tags => 320,
          ShedduellerDashboardJobColumnKind.Progress => 200,
          ShedduellerDashboardJobColumnKind.Disposition => 144,
          ShedduellerDashboardJobColumnKind.Groups => 180,
          ShedduellerDashboardJobColumnKind.Priority or ShedduellerDashboardJobColumnKind.Attempts => 56,
          ShedduellerDashboardJobColumnKind.Tag => 160,
          _ => 144,
      };

    public static int AutoFitMinimum(ShedduellerDashboardJobColumnKind kind)
      => kind switch
      {
          ShedduellerDashboardJobColumnKind.JobId => 64,
          ShedduellerDashboardJobColumnKind.Enqueued or ShedduellerDashboardJobColumnKind.TerminalTime => 128,
          ShedduellerDashboardJobColumnKind.State => 88,
          ShedduellerDashboardJobColumnKind.Queue => 72,
          ShedduellerDashboardJobColumnKind.Handler => 120,
          ShedduellerDashboardJobColumnKind.Tags => 120,
          ShedduellerDashboardJobColumnKind.Progress => 120,
          ShedduellerDashboardJobColumnKind.Disposition => 96,
          ShedduellerDashboardJobColumnKind.Groups => 120,
          ShedduellerDashboardJobColumnKind.Priority or ShedduellerDashboardJobColumnKind.Attempts => 48,
          ShedduellerDashboardJobColumnKind.Tag => 72,
          _ => MinimumOverride,
      };

    public static int AutoFitMaximum(ShedduellerDashboardJobColumnKind kind)
      => kind switch
      {
          ShedduellerDashboardJobColumnKind.JobId => 160,
          ShedduellerDashboardJobColumnKind.Enqueued or ShedduellerDashboardJobColumnKind.TerminalTime => 220,
          ShedduellerDashboardJobColumnKind.State => 160,
          ShedduellerDashboardJobColumnKind.Queue => 180,
          ShedduellerDashboardJobColumnKind.Handler => 420,
          ShedduellerDashboardJobColumnKind.Tags => 520,
          ShedduellerDashboardJobColumnKind.Progress => 360,
          ShedduellerDashboardJobColumnKind.Disposition => 360,
          ShedduellerDashboardJobColumnKind.Groups => 420,
          ShedduellerDashboardJobColumnKind.Priority or ShedduellerDashboardJobColumnKind.Attempts => 96,
          ShedduellerDashboardJobColumnKind.Tag => 320,
          _ => MaximumOverride,
      };

    public static int ClampAutoFit(
        ShedduellerDashboardJobColumnKind kind,
        int width)
      => Math.Clamp(width, AutoFitMinimum(kind), AutoFitMaximum(kind));

    public static int Total(IReadOnlyList<ShedduellerDashboardJobColumn> columns)
      => columns.Sum(Get);

    public static bool Set(
        List<ShedduellerDashboardJobColumn> columns,
        int index,
        int? width)
    {
        if (index < 0 || index >= columns.Count || !IsValid(width))
        {
            return false;
        }

        var normalizedWidth = width == Default(columns[index].Kind) ? null : width;
        if (columns[index].Width == normalizedWidth)
        {
            return false;
        }

        columns[index] = columns[index] with
        {
            Width = normalizedWidth,
        };
        return true;
    }

    public static bool Reset(List<ShedduellerDashboardJobColumn> columns)
    {
        var changed = false;
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].Width is null)
            {
                continue;
            }

            columns[index] = columns[index] with
            {
                Width = null,
            };
            changed = true;
        }

        return changed;
    }
}
