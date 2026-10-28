namespace Sheddueller.Dashboard.Internal;

using Sheddueller.Inspection.ConcurrencyGroups;

/// <summary>
/// The concurrency groups list filters: the condition switches, read by the store, and key text, matched against
/// the loaded groups.
/// </summary>
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

/// <summary>
/// The concurrency groups list's query string, and links to it.
/// </summary>
internal static class DashboardConcurrencyGroupFilterQuery
{
    public const string Path = "concurrency-groups";
    public const string KeyParameter = "key";
    public const string SaturatedParameter = "saturated";
    public const string RateLimitedParameter = "rateLimited";
    public const string BlockedParameter = "blocked";

    public static DashboardConcurrencyGroupFilters ParseUri(string uri)
    {
        var query = DashboardQueryString.ParseUri(uri);
        return new DashboardConcurrencyGroupFilters
        {
            GroupKey = DashboardQueryString.LastNonEmpty(query, KeyParameter) ?? string.Empty,
            SaturatedOnly = DashboardQueryString.IsTrue(query, SaturatedParameter),
            RateLimitedOnly = DashboardQueryString.IsTrue(query, RateLimitedParameter),
            HasBlockedJobsOnly = DashboardQueryString.IsTrue(query, BlockedParameter),
        };
    }

    public static string ToHref(DashboardConcurrencyGroupFilters filters)
      => DashboardQueryString.Build(
        Path,
        [
            (SaturatedParameter, filters.SaturatedOnly ? "true" : null),
            (RateLimitedParameter, filters.RateLimitedOnly ? "true" : null),
            (BlockedParameter, filters.HasBlockedJobsOnly ? "true" : null),
            (KeyParameter, filters.GroupKey),
        ]);

    /// <summary>
    /// The concurrency groups list filtered to one group key.
    /// </summary>
    public static string GroupHref(string groupKey)
      => ToHref(new DashboardConcurrencyGroupFilters { GroupKey = groupKey });
}
