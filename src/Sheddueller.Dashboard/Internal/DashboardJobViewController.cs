namespace Sheddueller.Dashboard.Internal;

using Microsoft.Extensions.Options;

internal sealed class DashboardJobViewController(IOptions<ShedduellerDashboardOptions> options)
{
    private readonly IOptions<ShedduellerDashboardOptions> _options = options;

    public List<DashboardResolvedJobView> Views { get; } = [];

    public List<DashboardStoredJobView> StoredViews { get; } = [];

    public string SelectedKey { get; set; } = DashboardJobViews.BuiltInKey;

    public string PreferredKey { get; set; } = DashboardJobViews.BuiltInKey;

    public bool IsDirty { get; set; }

    public IReadOnlyList<ShedduellerDashboardJobColumn> Initialize()
    {
        this.Views.Add(new DashboardResolvedJobView(
          DashboardJobViews.BuiltInKey,
          DashboardJobViews.BuiltInName,
          DashboardJobViewSource.BuiltIn,
          DashboardJobViews.BuiltInView));

        foreach (var configuredView in this._options.Value.JobViews)
        {
            var normalizedView = configuredView with
            {
                Name = configuredView.Name.Trim(),
            };
            this.Views.Add(new DashboardResolvedJobView(
              DashboardJobViews.SharedKey(normalizedView.Name),
              normalizedView.Name,
              DashboardJobViewSource.Shared,
              normalizedView));
        }

        var configuredDefault = string.IsNullOrWhiteSpace(this._options.Value.DefaultJobViewName)
          ? null
          : this.Views.FirstOrDefault(view =>
              view.Source == DashboardJobViewSource.Shared
              && string.Equals(view.Name, this._options.Value.DefaultJobViewName.Trim(), StringComparison.OrdinalIgnoreCase));
        var initialView = configuredDefault ?? this.Views[0];
        this.SelectedKey = initialView.Key;
        this.PreferredKey = initialView.Key;
        return DashboardJobViews.GetColumns(initialView.Definition);
    }

    public void RebuildPersonalViews()
    {
        this.Views.RemoveAll(view => view.Source == DashboardJobViewSource.Personal);
        foreach (var storedView in this.StoredViews)
        {
            this.Views.Add(new DashboardResolvedJobView(
              DashboardJobViews.PersonalKey(storedView.Id),
              storedView.View.Name,
              DashboardJobViewSource.Personal,
              storedView.View));
        }
    }

    public DashboardResolvedJobView ConfiguredDefaultView()
    {
        if (!string.IsNullOrWhiteSpace(this._options.Value.DefaultJobViewName)
            && this.Views.FirstOrDefault(view =>
                view.Source == DashboardJobViewSource.Shared
                && string.Equals(view.Name, this._options.Value.DefaultJobViewName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } configuredDefault)
        {
            return configuredDefault;
        }

        return this.Views[0];
    }

    public DashboardResolvedJobView? FindView(string key)
      => this.Views.FirstOrDefault(view => string.Equals(view.Key, key, StringComparison.Ordinal));

    public DashboardStoredJobView? FindStoredView(string key)
    {
        if (!key.StartsWith(DashboardJobViews.PersonalKeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = key[DashboardJobViews.PersonalKeyPrefix.Length..];
        return this.StoredViews.FirstOrDefault(view => string.Equals(view.Id, id, StringComparison.Ordinal));
    }

    public bool IsViewNameAvailable(
        string name,
        string? exceptKey)
      => this.Views.All(view =>
        string.Equals(view.Key, exceptKey, StringComparison.Ordinal)
        || !string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase));
}
