namespace Sheddueller.Dashboard.Internal;

internal static class DashboardJobViews
{
    public const string BuiltInKey = "built-in";
    public const string BuiltInName = "Default";
    public const string SharedKeyPrefix = "shared:";
    public const string PersonalKeyPrefix = "personal:";

    public static readonly IReadOnlyList<ShedduellerDashboardJobColumn> BuiltInColumns =
    [
        new(ShedduellerDashboardJobColumnKind.JobId),
        new(ShedduellerDashboardJobColumnKind.Enqueued),
        new(ShedduellerDashboardJobColumnKind.TerminalTime),
        new(ShedduellerDashboardJobColumnKind.State),
        new(ShedduellerDashboardJobColumnKind.Queue),
        new(ShedduellerDashboardJobColumnKind.Handler),
        new(ShedduellerDashboardJobColumnKind.Tags),
        new(ShedduellerDashboardJobColumnKind.Disposition),
        new(ShedduellerDashboardJobColumnKind.Groups),
        new(ShedduellerDashboardJobColumnKind.Priority),
        new(ShedduellerDashboardJobColumnKind.Attempts),
    ];

    public static readonly ShedduellerDashboardJobView BuiltInView = new(BuiltInName)
    {
        Columns = BuiltInColumns,
    };

    public static bool IsValid(ShedduellerDashboardOptions options)
    {
        if (options.JobViews is null)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuiltInName,
        };

        foreach (var view in options.JobViews)
        {
            if (view is null || !IsValid(view) || !names.Add(view.Name.Trim()))
            {
                return false;
            }
        }

        return string.IsNullOrWhiteSpace(options.DefaultJobViewName)
          || options.JobViews.Any(view => string.Equals(
              view.Name.Trim(),
              options.DefaultJobViewName.Trim(),
              StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsValid(ShedduellerDashboardJobView view)
    {
        if (view is null
            || string.IsNullOrWhiteSpace(view.Name)
            || !Enum.IsDefined(view.Sort)
            || view.States is null
            || view.States.Any(state => !Enum.IsDefined(state))
            || view.States.Count != view.States.Distinct().Count())
        {
            return false;
        }

        return view.Columns is null || IsValidColumns(view.Columns);
    }

    public static IReadOnlyList<ShedduellerDashboardJobColumn> GetColumns(ShedduellerDashboardJobView view)
      => NormalizeColumns(view.Columns ?? BuiltInColumns);

    public static DashboardJobFilters CreateFilters(ShedduellerDashboardJobView view)
    {
        var filters = new DashboardJobFilters();
        _ = filters.ReplaceStates(view.States);
        _ = filters.SetHandlerContains(view.HandlerContains ?? string.Empty);
        _ = filters.SetTagContains(view.TagContains ?? string.Empty);
        _ = filters.SetConcurrencyGroupContains(view.ConcurrencyGroupContains ?? string.Empty);
        _ = filters.SetSort(view.Sort);
        return filters;
    }

    public static ShedduellerDashboardJobView CreateView(
        string name,
        DashboardJobFilters filters,
        IReadOnlyList<ShedduellerDashboardJobColumn> columns)
      => new(name.Trim())
      {
          States = filters.SelectedStates,
          HandlerContains = Normalize(filters.HandlerContains),
          TagContains = Normalize(filters.TagContains),
          ConcurrencyGroupContains = Normalize(filters.ConcurrencyGroupContains),
          Sort = filters.Sort,
          Columns = NormalizeColumns(columns),
      };

    public static string SharedKey(string name)
      => string.Concat(SharedKeyPrefix, name.Trim());

    public static string PersonalKey(string id)
      => string.Concat(PersonalKeyPrefix, id);

    public static IReadOnlyList<JobTag> GetTagValues(
        IReadOnlyList<JobTag> tags,
        string tagName)
    {
        var values = new List<JobTag>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Name, tagName, StringComparison.OrdinalIgnoreCase)
                && seen.Add(tag.Value))
            {
                values.Add(tag);
            }
        }

        return values;
    }

    public static IReadOnlyList<JobTag> GetResidualTags(
        IReadOnlyList<JobTag> tags,
        IReadOnlyList<ShedduellerDashboardJobColumn> columns)
    {
        var promotedNames = columns
          .Where(column => column.Kind == ShedduellerDashboardJobColumnKind.Tag)
          .Select(column => column.TagName ?? string.Empty)
          .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return promotedNames.Count == 0
          ? tags
          : [.. tags.Where(tag => !promotedNames.Contains(tag.Name))];
    }

    public static string Heading(ShedduellerDashboardJobColumn column)
      => column.Kind == ShedduellerDashboardJobColumnKind.Tag
        ? Normalize(column.Heading) ?? column.TagName!
        : column.Kind switch
        {
            ShedduellerDashboardJobColumnKind.JobId => "Job ID",
            ShedduellerDashboardJobColumnKind.Enqueued => "Enqueued",
            ShedduellerDashboardJobColumnKind.TerminalTime => "Terminal Time",
            ShedduellerDashboardJobColumnKind.State => "State",
            ShedduellerDashboardJobColumnKind.Queue => "Queue",
            ShedduellerDashboardJobColumnKind.Handler => "Handler",
            ShedduellerDashboardJobColumnKind.Tags => "Tags",
            ShedduellerDashboardJobColumnKind.Progress => "Progress",
            ShedduellerDashboardJobColumnKind.Disposition => "Disposition",
            ShedduellerDashboardJobColumnKind.Groups => "Groups",
            ShedduellerDashboardJobColumnKind.Priority => "Pri",
            ShedduellerDashboardJobColumnKind.Attempts => "Att",
            _ => column.Kind.ToString(),
        };

    private static bool IsValidColumns(IReadOnlyList<ShedduellerDashboardJobColumn> columns)
    {
        if (columns.Count == 0)
        {
            return false;
        }

        var builtInKinds = new HashSet<ShedduellerDashboardJobColumnKind>();
        var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasJobId = false;
        foreach (var column in columns)
        {
            if (column is null
                || !Enum.IsDefined(column.Kind)
                || !DashboardJobColumnWidths.IsValid(column.Width))
            {
                return false;
            }

            if (column.Kind == ShedduellerDashboardJobColumnKind.Tag)
            {
                if (string.IsNullOrWhiteSpace(column.TagName) || !tagNames.Add(column.TagName.Trim()))
                {
                    return false;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(column.TagName)
                || !string.IsNullOrWhiteSpace(column.Heading)
                || !builtInKinds.Add(column.Kind))
            {
                return false;
            }

            hasJobId |= column.Kind == ShedduellerDashboardJobColumnKind.JobId;
        }

        return hasJobId;
    }

    private static IReadOnlyList<ShedduellerDashboardJobColumn> NormalizeColumns(
        IReadOnlyList<ShedduellerDashboardJobColumn> columns)
      =>
      [
          .. columns.Select(column => column.Kind == ShedduellerDashboardJobColumnKind.Tag
            ? column with
            {
                TagName = column.TagName!.Trim(),
                Heading = Normalize(column.Heading),
            }
            : new ShedduellerDashboardJobColumn(column.Kind)
            {
                Width = column.Width,
            }),
      ];

    private static string? Normalize(string? value)
      => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal enum DashboardJobViewSource
{
    BuiltIn,
    Shared,
    Personal,
}

internal sealed record DashboardResolvedJobView(
    string Key,
    string Name,
    DashboardJobViewSource Source,
    ShedduellerDashboardJobView Definition);

internal sealed class DashboardJobViewStoragePayload
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string? PreferredViewKey { get; set; }

    public List<DashboardStoredJobView> Views { get; set; } = [];
}

internal sealed class DashboardStoredJobView
{
    public string Id { get; set; } = string.Empty;

    public ShedduellerDashboardJobView View { get; set; } = new(string.Empty);
}
