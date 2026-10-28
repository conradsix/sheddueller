namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

using Microsoft.AspNetCore.WebUtilities;

using Sheddueller.Inspection.ConcurrencyGroups;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Nodes;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Storage;

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

    public static DashboardJobFilters ParseUri(string uri)
      => ParseQuery(new Uri(uri, UriKind.Absolute).Query);

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

        if (TryGetLastNonEmptyValue(query, HandlerParameter, out var handler))
        {
            _ = filters.SetHandlerContains(handler);
        }

        if (TryGetLastNonEmptyValue(query, TagParameter, out var tag))
        {
            _ = filters.SetTagContains(tag);
        }

        if (TryGetLastNonEmptyValue(query, GroupParameter, out var group))
        {
            _ = filters.SetConcurrencyGroupContains(group);
        }

        if (TryGetLastNonEmptyValue(query, SortParameter, out var sortValue)
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
        var queryParts = new List<string>();

        foreach (var state in filters.SelectedStates)
        {
            AddQueryPart(queryParts, StateParameter, state.ToString());
        }

        AddQueryPart(queryParts, HandlerParameter, filters.HandlerContains);
        AddQueryPart(queryParts, TagParameter, filters.TagContains);
        AddQueryPart(queryParts, GroupParameter, filters.ConcurrencyGroupContains);
        if (filters.Sort != JobInspectionSort.Operational || (forceExplicitFilters && queryParts.Count == 0))
        {
            AddQueryPart(queryParts, SortParameter, filters.Sort.ToString());
        }

        AddQueryPart(queryParts, ViewParameter, viewKey ?? string.Empty);
        if (take is { } takeValue)
        {
            AddQueryPart(queryParts, TakeParameter, takeValue.ToString(CultureInfo.InvariantCulture));
        }

        return queryParts.Count == 0
          ? JobsPath
          : string.Concat(JobsPath, "?", string.Join("&", queryParts));
    }

    public static string StateHref(JobState state)
    {
        var filters = new DashboardJobFilters();
        _ = filters.ReplaceStates([state]);
        return ToHref(filters);
    }

    public static string HandlerHref(string handler)
    {
        var filters = new DashboardJobFilters();
        _ = filters.SetHandlerContains(handler);
        return ToHref(filters);
    }

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

    public static string WithStateHref(
        DashboardJobFilters filters,
        JobState state,
        string? viewKey = null)
    {
        var next = filters.Clone();
        _ = next.ReplaceStates([state]);
        return ToHref(next, viewKey, take: null, forceExplicitFilters: false);
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

    public static bool TryGetLastNonEmptyValue(
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query,
        string parameterName,
        out string value)
    {
        value = string.Empty;
        if (!query.TryGetValue(parameterName, out var values))
        {
            return false;
        }

        for (var i = values.Count - 1; i >= 0; i--)
        {
            var candidate = values[i];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                value = candidate.Trim();
                return true;
            }
        }

        return false;
    }

    private static void AddQueryPart(
        List<string> queryParts,
        string parameterName,
        string value)
    {
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalizedValue is null)
        {
            return;
        }

        queryParts.Add(string.Concat(
          Uri.EscapeDataString(parameterName),
          "=",
          Uri.EscapeDataString(normalizedValue)));
    }
}

internal sealed class DashboardScheduleFilters
{
    public const string ActiveStateFilter = "active";
    public const string PausedStateFilter = "paused";

    public string ScheduleKey { get; set; } = string.Empty;

    public string PauseState { get; set; } = string.Empty;

    public bool HasAppliedFilters
      => !string.IsNullOrWhiteSpace(this.ScheduleKey)
        || !string.IsNullOrWhiteSpace(this.PauseState);

    public bool CopyFrom(DashboardScheduleFilters other)
    {
        if (string.Equals(this.ScheduleKey, other.ScheduleKey, StringComparison.Ordinal)
            && string.Equals(this.PauseState, other.PauseState, StringComparison.Ordinal))
        {
            return false;
        }

        this.ScheduleKey = other.ScheduleKey;
        this.PauseState = other.PauseState;
        return true;
    }

    public ScheduleInspectionQuery ToQuery(
        int pageSize,
        string? continuationToken)
      => new(
        Normalize(this.ScheduleKey),
        ParsePauseState(this.PauseState),
        ServiceType: null,
        MethodName: null,
        Tag: null,
        pageSize,
        continuationToken);

    private static bool? ParsePauseState(string value)
      => value switch
      {
          PausedStateFilter => true,
          ActiveStateFilter => false,
          _ => null,
      };

    private static string? Normalize(string value)
      => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class DashboardNodeFilters
{
    public string State { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public NodeHealthState? StateFilter
      => Enum.TryParse<NodeHealthState>(this.State, out var state) ? state : null;

    public bool CopyFrom(DashboardNodeFilters other)
    {
        if (string.Equals(this.State, other.State, StringComparison.Ordinal)
            && string.Equals(this.NodeId, other.NodeId, StringComparison.Ordinal))
        {
            return false;
        }

        this.State = other.State;
        this.NodeId = other.NodeId;
        return true;
    }

    public IReadOnlyList<NodeInspectionSummary> ApplyClientFilter(IReadOnlyList<NodeInspectionSummary> nodes)
      => string.IsNullOrWhiteSpace(this.NodeId)
        ? nodes
        : [.. nodes.Where(node => node.NodeId.Contains(this.NodeId, StringComparison.OrdinalIgnoreCase))];

    public NodeInspectionQuery ToQuery(
        int pageSize,
        string? continuationToken)
      => new(this.StateFilter, pageSize, continuationToken);
}

internal sealed class DashboardConcurrencyGroupFilters
{
    public string GroupKey { get; set; } = string.Empty;

    public bool SaturatedOnly { get; set; }

    public bool HasBlockedJobsOnly { get; set; }

    public bool RateLimitedOnly { get; set; }

    public bool? SaturatedFilter
      => this.SaturatedOnly ? true : null;

    public bool? HasBlockedJobsFilter
      => this.HasBlockedJobsOnly ? true : null;

    public bool? RateLimitedFilter
      => this.RateLimitedOnly ? true : null;

    public bool CopyFrom(DashboardConcurrencyGroupFilters other)
    {
        if (string.Equals(this.GroupKey, other.GroupKey, StringComparison.Ordinal)
            && this.SaturatedOnly == other.SaturatedOnly
            && this.HasBlockedJobsOnly == other.HasBlockedJobsOnly
            && this.RateLimitedOnly == other.RateLimitedOnly)
        {
            return false;
        }

        this.GroupKey = other.GroupKey;
        this.SaturatedOnly = other.SaturatedOnly;
        this.HasBlockedJobsOnly = other.HasBlockedJobsOnly;
        this.RateLimitedOnly = other.RateLimitedOnly;
        return true;
    }

    public IReadOnlyList<ConcurrencyGroupInspectionSummary> ApplyClientFilter(
        IReadOnlyList<ConcurrencyGroupInspectionSummary> groups)
      => string.IsNullOrWhiteSpace(this.GroupKey)
        ? groups
        : [.. groups.Where(group => group.GroupKey.Contains(this.GroupKey, StringComparison.OrdinalIgnoreCase))];

    public ConcurrencyGroupInspectionQuery ToQuery(
        int pageSize,
        string? continuationToken)
      => new(
        GroupKey: null,
        IsSaturated: this.SaturatedFilter,
        HasBlockedJobs: this.HasBlockedJobsFilter,
        PageSize: pageSize,
        ContinuationToken: continuationToken,
        IsRateLimited: this.RateLimitedFilter);
}
