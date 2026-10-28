namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

/// <summary>
/// What became of the personal views read from browser storage.
/// </summary>
internal enum DashboardJobViewStorageLoad
{
    /// <summary>
    /// Nothing was stored.
    /// </summary>
    Empty,

    /// <summary>
    /// Stored in a format this version can't read, so nothing was loaded.
    /// </summary>
    UnsupportedVersion,

    /// <summary>
    /// Every stored view was loaded.
    /// </summary>
    Loaded,

    /// <summary>
    /// The valid views were loaded and the rest ignored.
    /// </summary>
    SomeViewsInvalid,
}

/// <summary>
/// A named group of views in the jobs page's view menu.
/// </summary>
internal sealed record DashboardJobViewGroup(
    string Label,
    IReadOnlyList<DashboardResolvedJobView> Views);

/// <summary>
/// The job views one jobs page offers: the built-in view, the views configured for everyone, and the user's own
/// views from browser storage. Tracks which view is selected, which one opens by default, and whether the list has
/// moved away from the selected view's filters or columns. Reading and writing browser storage is the page's job.
/// </summary>
internal sealed class DashboardJobViewSession
{
    private const string StorageKeyPrefix = "sheddueller.dashboard.jobViews.v1:";

    private static readonly (string Label, DashboardJobViewSource Source)[] GroupOrder =
    [
        ("Built-in", DashboardJobViewSource.BuiltIn),
        ("Shared", DashboardJobViewSource.Shared),
        ("My views", DashboardJobViewSource.Personal),
    ];

    private readonly List<DashboardResolvedJobView> _views = [];
    private readonly List<DashboardStoredJobView> _storedViews = [];

    public DashboardJobViewSession(ShedduellerDashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        this._views.Add(new DashboardResolvedJobView(
          DashboardJobViews.BuiltInKey,
          DashboardJobViews.BuiltInName,
          DashboardJobViewSource.BuiltIn,
          DashboardJobViews.BuiltInView));

        foreach (var configuredView in options.JobViews)
        {
            var name = configuredView.Name.Trim();
            this._views.Add(new DashboardResolvedJobView(
              DashboardJobViews.SharedKey(name),
              name,
              DashboardJobViewSource.Shared,
              configuredView with { Name = name }));
        }

        var defaultName = options.DefaultJobViewName?.Trim();
        this.ConfiguredDefaultView = string.IsNullOrEmpty(defaultName)
          ? this._views[0]
          : this._views.FirstOrDefault(view =>
              view.Source == DashboardJobViewSource.Shared
              && string.Equals(view.Name, defaultName, StringComparison.OrdinalIgnoreCase))
            ?? this._views[0];
        this.SelectedKey = this.ConfiguredDefaultView.Key;
        this.PreferredKey = this.ConfiguredDefaultView.Key;
    }

    /// <summary>
    /// The configured default view, or the built-in view when none is configured.
    /// </summary>
    public DashboardResolvedJobView ConfiguredDefaultView { get; }

    public string SelectedKey { get; private set; }

    /// <summary>
    /// The view that opens when the URL names none.
    /// </summary>
    public string PreferredKey { get; private set; }

    /// <summary>
    /// The filters or columns differ from the selected view's saved definition.
    /// </summary>
    public bool IsDirty { get; private set; }

    public IReadOnlyList<DashboardResolvedJobView> Views
      => this._views;

    public DashboardResolvedJobView? CurrentView
      => this.FindView(this.SelectedKey);

    /// <summary>
    /// The preferred view, or the configured default when the preferred view is gone.
    /// </summary>
    public DashboardResolvedJobView PreferredOrDefaultView
      => this.FindView(this.PreferredKey) ?? this.ConfiguredDefaultView;

    public bool IsPersonalView
      => this.CurrentView?.Source == DashboardJobViewSource.Personal;

    public bool CanSaveCurrentView
      => this.IsDirty && this.IsPersonalView;

    public bool IsCurrentViewPreferred
      => string.Equals(this.SelectedKey, this.PreferredKey, StringComparison.Ordinal);

    public bool CanSetPreferredView
      => !this.IsDirty && !this.IsCurrentViewPreferred;

    public string SuggestedViewName
      => this.CurrentView?.Source == DashboardJobViewSource.BuiltIn
        ? "My view"
        : string.Concat(this.CurrentView?.Name ?? "Job view", " copy");

    /// <summary>
    /// The <c>view</c> URL value for the selected view; see <see cref="DashboardJobListLocation.ViewParameterValue"/>.
    /// </summary>
    public string? ViewParameter
      => DashboardJobListLocation.ViewParameterValue(this.SelectedKey, this.PreferredKey, this.ConfiguredDefaultView.Key);

    /// <summary>
    /// The views by source for the view menu; sources without views are left out.
    /// </summary>
    public IReadOnlyList<DashboardJobViewGroup> ViewGroups
      =>
      [
          .. GroupOrder
            .Select(group => new DashboardJobViewGroup(
              group.Label,
              [.. this._views.Where(view => view.Source == group.Source)]))
            .Where(group => group.Views.Count > 0),
      ];

    /// <summary>
    /// The browser storage key for the views of the dashboard mounted at <paramref name="baseUri"/>, so dashboards
    /// on one host keep separate views.
    /// </summary>
    public static string StorageKey(string baseUri)
      => string.Concat(StorageKeyPrefix, new Uri(baseUri, UriKind.Absolute).AbsolutePath.TrimEnd('/'));

    public DashboardResolvedJobView? FindView(string key)
      => this._views.FirstOrDefault(view => string.Equals(view.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// The view a URL asks for, falling back to the preferred view and then the configured default.
    /// </summary>
    public DashboardResolvedJobView ResolveView(string? key)
      => (key is null ? null : this.FindView(key)) ?? this.PreferredOrDefaultView;

    /// <summary>
    /// The filters of the selected view (or of the configured default, when the selected view is gone).
    /// </summary>
    public DashboardJobFilters CurrentViewFilters()
      => DashboardJobViews.CreateFilters((this.CurrentView ?? this.ConfiguredDefaultView).Definition);

    /// <summary>
    /// An error for a view name that another view already uses, ignoring case; <see langword="null" /> when it is
    /// free. The view with <paramref name="exceptKey"/> (the one being renamed) may keep its own name.
    /// </summary>
    public string? ValidateViewName(
        string name,
        string? exceptKey)
      => this._views.All(view =>
          string.Equals(view.Key, exceptKey, StringComparison.Ordinal)
          || !string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
        ? null
        : string.Concat("A view named “", name, "” already exists.");

    /// <summary>
    /// Adds the personal views read from browser storage. A stored view is ignored when its ID isn't a GUID or
    /// repeats, its definition is invalid, or its name is already taken. The stored preferred view applies when it
    /// exists.
    /// </summary>
    public DashboardJobViewStorageLoad Load(DashboardJobViewStoragePayload? payload)
    {
        if (payload is null)
        {
            return DashboardJobViewStorageLoad.Empty;
        }

        if (payload.Version != DashboardJobViewStoragePayload.CurrentVersion)
        {
            return DashboardJobViewStorageLoad.UnsupportedVersion;
        }

        var invalidViewFound = false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = this._views.Select(view => view.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var storedView in payload.Views ?? [])
        {
            if (storedView is null
                || !Guid.TryParseExact(storedView.Id, "D", out _)
                || !ids.Add(storedView.Id)
                || !DashboardJobViews.IsValid(storedView.View)
                || !names.Add(storedView.View.Name.Trim()))
            {
                invalidViewFound = true;
                continue;
            }

            storedView.View = storedView.View with
            {
                Name = storedView.View.Name.Trim(),
            };
            this._storedViews.Add(storedView);
        }

        this.RebuildPersonalViews();
        if (!string.IsNullOrWhiteSpace(payload.PreferredViewKey)
            && this.FindView(payload.PreferredViewKey) is { } preferredView)
        {
            this.PreferredKey = preferredView.Key;
        }

        return invalidViewFound ? DashboardJobViewStorageLoad.SomeViewsInvalid : DashboardJobViewStorageLoad.Loaded;
    }

    /// <summary>
    /// The personal views and preferred view, to write to browser storage.
    /// </summary>
    public DashboardJobViewStoragePayload ToPayload()
      => new()
      {
          PreferredViewKey = this.PreferredKey,
          Views = [.. this._storedViews],
      };

    public void Select(DashboardResolvedJobView view)
      => this.SelectedKey = view.Key;

    /// <summary>
    /// Records whether <paramref name="filters"/> and <paramref name="columns"/> differ from the selected view's
    /// definition. Whitespace-only differences in text filters don't count, so a URL that reproduces the view, or a
    /// change that is undone, isn't reported as an edit.
    /// </summary>
    public void UpdateDirty(
        DashboardJobFilters filters,
        IReadOnlyList<ShedduellerDashboardJobColumn> columns)
    {
        var view = this.CurrentView;
        this.IsDirty = view is not null
          && (!filters.Matches(DashboardJobViews.CreateFilters(view.Definition))
            || !columns.SequenceEqual(DashboardJobViews.GetColumns(view.Definition)));
    }

    /// <summary>
    /// Saves the filters and columns as a new personal view and selects it.
    /// </summary>
    public DashboardResolvedJobView SaveAs(
        string name,
        DashboardJobFilters filters,
        IReadOnlyList<ShedduellerDashboardJobColumn> columns)
    {
        var id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        this._storedViews.Add(new DashboardStoredJobView
        {
            Id = id,
            View = DashboardJobViews.CreateView(name, filters, columns),
        });
        this.RebuildPersonalViews();
        this.SelectedKey = DashboardJobViews.PersonalKey(id);
        return this.CurrentView!;
    }

    /// <summary>
    /// Saves the filters and columns over the selected personal view; <see langword="null" /> when there is nothing
    /// to save (the view isn't personal, or has no changes).
    /// </summary>
    public DashboardResolvedJobView? SaveCurrent(
        DashboardJobFilters filters,
        IReadOnlyList<ShedduellerDashboardJobColumn> columns)
    {
        if (!this.CanSaveCurrentView || this.FindStoredView(this.SelectedKey) is not { } storedView)
        {
            return null;
        }

        storedView.View = DashboardJobViews.CreateView(storedView.View.Name, filters, columns);
        this.RebuildPersonalViews();
        return this.CurrentView;
    }

    /// <summary>
    /// Renames the selected personal view; <see langword="null" /> when it isn't personal.
    /// </summary>
    public DashboardResolvedJobView? RenameCurrent(string name)
    {
        if (this.FindStoredView(this.SelectedKey) is not { } storedView)
        {
            return null;
        }

        storedView.View = storedView.View with
        {
            Name = name,
        };
        this.RebuildPersonalViews();
        return this.CurrentView;
    }

    /// <summary>
    /// Deletes the selected personal view; <see langword="null" /> when it isn't personal. When it was the
    /// preferred view, the configured default becomes preferred. The caller then selects another view.
    /// </summary>
    public DashboardResolvedJobView? DeleteCurrent()
    {
        if (this.CurrentView is not { Source: DashboardJobViewSource.Personal } view
            || this.FindStoredView(this.SelectedKey) is not { } storedView)
        {
            return null;
        }

        _ = this._storedViews.Remove(storedView);
        this.RebuildPersonalViews();
        if (string.Equals(this.PreferredKey, view.Key, StringComparison.Ordinal))
        {
            this.PreferredKey = this.ConfiguredDefaultView.Key;
        }

        return view;
    }

    /// <summary>
    /// Makes the selected view open by default; <see langword="null" /> when it can't be (it has unsaved changes,
    /// or already is).
    /// </summary>
    public DashboardResolvedJobView? SetCurrentAsPreferred()
    {
        if (!this.CanSetPreferredView || this.CurrentView is not { } view)
        {
            return null;
        }

        this.PreferredKey = view.Key;
        return view;
    }

    private void RebuildPersonalViews()
    {
        this._views.RemoveAll(view => view.Source == DashboardJobViewSource.Personal);
        foreach (var storedView in this._storedViews)
        {
            this._views.Add(new DashboardResolvedJobView(
              DashboardJobViews.PersonalKey(storedView.Id),
              storedView.View.Name,
              DashboardJobViewSource.Personal,
              storedView.View));
        }
    }

    private DashboardStoredJobView? FindStoredView(string key)
    {
        if (!key.StartsWith(DashboardJobViews.PersonalKeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = key[DashboardJobViews.PersonalKeyPrefix.Length..];
        return this._storedViews.FirstOrDefault(view => string.Equals(view.Id, id, StringComparison.Ordinal));
    }
}
