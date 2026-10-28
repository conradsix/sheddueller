namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

using Microsoft.AspNetCore.WebUtilities;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Storage;

/// <summary>
/// The jobs list filters: states, handler, tag, and concurrency group text, and the sort.
/// </summary>
internal sealed class DashboardJobFilters
{
    private readonly HashSet<JobState> _states = [];
    private string _handlerContains = string.Empty;
    private string _tagContains = string.Empty;
    private string _concurrencyGroupContains = string.Empty;

    public string HandlerContains => this._handlerContains;

    public string TagContains => this._tagContains;

    public string ConcurrencyGroupContains => this._concurrencyGroupContains;

    public JobInspectionSort Sort { get; private set; } = JobInspectionSort.Operational;

    public IReadOnlyList<JobState> SelectedStates
      => [.. Enum.GetValues<JobState>().Where(this._states.Contains)];

    public bool HasAppliedFilters
      => this._states.Count > 0
        || !string.IsNullOrWhiteSpace(this.HandlerContains)
        || !string.IsNullOrWhiteSpace(this.TagContains)
        || !string.IsNullOrWhiteSpace(this.ConcurrencyGroupContains);

    public bool IsStateSelected(JobState state)
      => this._states.Contains(state);

    public bool SetStateSelected(
        JobState state,
        bool selected)
      => selected ? this._states.Add(state) : this._states.Remove(state);

    public bool SetHandlerContains(string value)
      => SetTextFilter(ref this._handlerContains, value);

    public bool SetTagContains(string value)
      => SetTextFilter(ref this._tagContains, value);

    public bool SetConcurrencyGroupContains(string value)
      => SetTextFilter(ref this._concurrencyGroupContains, value);

    public bool SetSort(JobInspectionSort sort)
    {
        if (this.Sort == sort)
        {
            return false;
        }

        this.Sort = sort;
        return true;
    }

    public bool ReplaceStates(IEnumerable<JobState> states)
    {
        var nextStates = states.ToHashSet();
        if (this._states.SetEquals(nextStates))
        {
            return false;
        }

        this._states.Clear();
        foreach (var state in nextStates)
        {
            this._states.Add(state);
        }

        return true;
    }

    public bool ReplaceWith(DashboardJobFilters filters)
    {
        var changed = !this.SelectedStates.SequenceEqual(filters.SelectedStates)
          || !string.Equals(this.HandlerContains, filters.HandlerContains, StringComparison.Ordinal)
          || !string.Equals(this.TagContains, filters.TagContains, StringComparison.Ordinal)
          || !string.Equals(this.ConcurrencyGroupContains, filters.ConcurrencyGroupContains, StringComparison.Ordinal)
          || this.Sort != filters.Sort;

        if (!changed)
        {
            return false;
        }

        this._states.Clear();
        foreach (var state in filters.SelectedStates)
        {
            this._states.Add(state);
        }

        this._handlerContains = filters.HandlerContains;
        this._tagContains = filters.TagContains;
        this._concurrencyGroupContains = filters.ConcurrencyGroupContains;
        this.Sort = filters.Sort;
        return true;
    }

    public bool Matches(DashboardJobFilters other)
      => this.SelectedStates.SequenceEqual(other.SelectedStates)
        && string.Equals(Normalize(this.HandlerContains), Normalize(other.HandlerContains), StringComparison.Ordinal)
        && string.Equals(Normalize(this.TagContains), Normalize(other.TagContains), StringComparison.Ordinal)
        && string.Equals(Normalize(this.ConcurrencyGroupContains), Normalize(other.ConcurrencyGroupContains), StringComparison.Ordinal)
        && this.Sort == other.Sort;

    public DashboardJobFilters Clone()
    {
        var clone = new DashboardJobFilters();
        _ = clone.ReplaceWith(this);
        return clone;
    }

    public void Clear()
    {
        this._states.Clear();
        this._handlerContains = string.Empty;
        this._tagContains = string.Empty;
        this._concurrencyGroupContains = string.Empty;
    }

    public JobInspectionQuery ToQuery(
        int pageSize,
        string? continuationToken)
      => new(
        this._states.Count == 0 ? null : [.. Enum.GetValues<JobState>().Where(this._states.Contains)],
        Normalize(this.HandlerContains),
        Normalize(this.TagContains),
        Normalize(this.ConcurrencyGroupContains),
        pageSize,
        continuationToken,
        this.Sort);

    private static bool SetTextFilter(
        ref string field,
        string value)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return false;
        }

        field = value;
        return true;
    }

    private static string? Normalize(string value)
      => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The jobs list's query string, and links to the jobs list with filters applied.
/// </summary>
internal static class DashboardJobFilterQuery
{
    public const string StateParameter = "state";
    public const string HandlerParameter = "handler";
    public const string TagParameter = "tag";
    public const string GroupParameter = "group";
    public const string SortParameter = "sort";
    public const string ViewParameter = "view";
    public const string TakeParameter = "take";
    public const string JobsPath = "jobs";

    public static bool HasRecognizedQuery(string queryString)
    {
        var query = QueryHelpers.ParseQuery(queryString);
        return query.ContainsKey(StateParameter)
          || query.ContainsKey(HandlerParameter)
          || query.ContainsKey(TagParameter)
          || query.ContainsKey(GroupParameter)
          || query.ContainsKey(SortParameter);
    }

    public static DashboardJobFilters ParseQuery(string queryString)
    {
        var filters = new DashboardJobFilters();
        var query = QueryHelpers.ParseQuery(queryString);

        if (query.TryGetValue(StateParameter, out var stateValues))
        {
            var states = new HashSet<JobState>();
            foreach (var stateValue in stateValues)
            {
                if (Enum.TryParse<JobState>(stateValue, ignoreCase: true, out var state))
                {
                    states.Add(state);
                }
            }

            _ = filters.ReplaceStates(states);
        }

        if (DashboardQueryString.LastNonEmpty(query, HandlerParameter) is { } handler)
        {
            _ = filters.SetHandlerContains(handler);
        }

        if (DashboardQueryString.LastNonEmpty(query, TagParameter) is { } tag)
        {
            _ = filters.SetTagContains(tag);
        }

        if (DashboardQueryString.LastNonEmpty(query, GroupParameter) is { } group)
        {
            _ = filters.SetConcurrencyGroupContains(group);
        }

        if (DashboardQueryString.LastNonEmpty(query, SortParameter) is { } sortValue
            && Enum.TryParse<JobInspectionSort>(sortValue, ignoreCase: true, out var sort)
            && Enum.IsDefined(sort))
        {
            _ = filters.SetSort(sort);
        }

        return filters;
    }

    public static string ToHref(DashboardJobFilters filters)
      => ToHref(filters, viewKey: null, take: null, forceExplicitFilters: false);

    /// <summary>
    /// Builds a jobs list URL. Filters are always written explicitly; when there is nothing to write and
    /// <paramref name="forceExplicitFilters"/> is set, the sort is written so the URL still means "these exact
    /// (empty) filters" rather than "the view's filters".
    /// </summary>
    public static string ToHref(
        DashboardJobFilters filters,
        string? viewKey,
        int? take,
        bool forceExplicitFilters)
    {
        var writeSort = filters.Sort != JobInspectionSort.Operational || (forceExplicitFilters && !filters.HasAppliedFilters);
        return DashboardQueryString.Build(
          JobsPath,
          [
              .. filters.SelectedStates.Select(state => (StateParameter, (string?)state.ToString())),
              (HandlerParameter, filters.HandlerContains),
              (TagParameter, filters.TagContains),
              (GroupParameter, filters.ConcurrencyGroupContains),
              (SortParameter, writeSort ? filters.Sort.ToString() : null),
              (ViewParameter, viewKey),
              (TakeParameter, take?.ToString(CultureInfo.InvariantCulture)),
          ]);
    }

    public static string StateHref(JobState state)
    {
        var filters = new DashboardJobFilters();
        _ = filters.ReplaceStates([state]);
        return ToHref(filters);
    }

    /// <summary>
    /// Failed jobs, newest failure first.
    /// </summary>
    public static string RecentlyFailedHref()
    {
        var filters = new DashboardJobFilters();
        _ = filters.ReplaceStates([JobState.Failed]);
        _ = filters.SetSort(JobInspectionSort.NewestFirst);
        return ToHref(filters);
    }

    public static string HandlerHref(string handler)
    {
        var filters = new DashboardJobFilters();
        _ = filters.SetHandlerContains(handler);
        return ToHref(filters);
    }

    /// <summary>
    /// Jobs with the same handler as <paramref name="job"/>.
    /// </summary>
    public static string HandlerHref(JobInspectionSummary job)
      => HandlerHref(DashboardFormat.ShortHandler(job));

    /// <summary>
    /// Jobs with the handler <paramref name="schedule"/> enqueues.
    /// </summary>
    public static string HandlerHref(ScheduleInspectionSummary schedule)
      => HandlerHref(DashboardFormat.ShortHandler(schedule));

    public static string TagHref(string tag)
    {
        var filters = new DashboardJobFilters();
        _ = filters.SetTagContains(tag);
        return ToHref(filters);
    }

    public static string GroupHref(string group)
    {
        var filters = new DashboardJobFilters();
        _ = filters.SetConcurrencyGroupContains(group);
        return ToHref(filters);
    }

    /// <summary>
    /// Queued jobs in a concurrency group, which includes the jobs it blocks.
    /// </summary>
    public static string QueuedInGroupHref(string group)
    {
        var filters = new DashboardJobFilters();
        _ = filters.SetConcurrencyGroupContains(group);
        _ = filters.SetStateSelected(JobState.Queued, selected: true);
        return ToHref(filters);
    }

    public static string WithHandlerHref(
        DashboardJobFilters filters,
        string handler,
        string? viewKey = null)
    {
        var next = filters.Clone();
        _ = next.SetHandlerContains(handler);
        return ToHref(next, viewKey, take: null, forceExplicitFilters: false);
    }

    public static string WithTagHref(
        DashboardJobFilters filters,
        string tag,
        string? viewKey = null)
    {
        var next = filters.Clone();
        _ = next.SetTagContains(tag);
        return ToHref(next, viewKey, take: null, forceExplicitFilters: false);
    }

    public static string WithGroupHref(
        DashboardJobFilters filters,
        string group,
        string? viewKey = null)
    {
        var next = filters.Clone();
        _ = next.SetConcurrencyGroupContains(group);
        return ToHref(next, viewKey, take: null, forceExplicitFilters: false);
    }
}

/// <summary>
/// The jobs list state carried by a URL: explicit filters (or <see langword="null"/> to use the view's filters),
/// the selected view key (or <see langword="null"/> for the user's preferred view), and the loaded depth.
/// </summary>
internal sealed record DashboardJobListLocation(
    DashboardJobFilters? Filters,
    string? ViewKey,
    int Take)
{
    public const int DefaultTake = 25;
    public const int MaximumTake = 1000;

    public static DashboardJobListLocation Parse(string uri)
    {
        var queryString = new Uri(uri, UriKind.Absolute).Query;
        var query = QueryHelpers.ParseQuery(queryString);
        var filters = DashboardJobFilterQuery.HasRecognizedQuery(queryString)
          ? DashboardJobFilterQuery.ParseQuery(queryString)
          : null;
        var viewKey = DashboardQueryString.LastNonEmpty(query, DashboardJobFilterQuery.ViewParameter);
        return new DashboardJobListLocation(filters, viewKey, ParseTake(DashboardQueryString.LastNonEmpty(query, DashboardJobFilterQuery.TakeParameter)));
    }

    public static int NormalizeTake(int take)
      => Math.Clamp(take, DefaultTake, MaximumTake);

    /// <summary>
    /// Builds the URL for the current list state. <paramref name="viewParameter"/> comes from
    /// <see cref="ViewParameterValue"/>; the depth is written only when more than one page is loaded.
    /// </summary>
    public static string ToHref(
        DashboardJobFilters filters,
        DashboardJobFilters viewFilters,
        string? viewParameter,
        int take)
      => DashboardJobFilterQuery.ToHref(
        filters,
        viewParameter,
        take > DefaultTake ? NormalizeTake(take) : null,
        forceExplicitFilters: !filters.Matches(viewFilters));

    /// <summary>
    /// The <c>view</c> query value for the selected view. A URL without one means "the preferred view", so the key
    /// is omitted only when the selected view is both the user's preferred view and the configured default; any
    /// other view is named so the URL restores it (for other users too, when it is a shared view).
    /// </summary>
    public static string? ViewParameterValue(
        string selectedViewKey,
        string preferredViewKey,
        string configuredDefaultViewKey)
      => string.Equals(selectedViewKey, preferredViewKey, StringComparison.Ordinal)
        && string.Equals(selectedViewKey, configuredDefaultViewKey, StringComparison.Ordinal)
          ? null
          : selectedViewKey;

    private static int ParseTake(string? value)
      => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var take)
        ? NormalizeTake(take)
        : DefaultTake;
}
