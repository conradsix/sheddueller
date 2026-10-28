namespace Sheddueller.Dashboard.Internal;

/// <summary>
/// Circuit-scoped navigation memory. The jobs list records its current URL here so job detail can link back
/// to the exact list (filters, view, and depth) the user came from.
/// </summary>
internal sealed class DashboardNavigationState
{
    public const string DefaultJobsListHref = DashboardJobFilterQuery.JobsPath;

    public string JobsListHref
    {
        get;
        set => field = value is not null && DashboardQueryString.IsPath(value, DashboardJobFilterQuery.JobsPath)
          ? value
          : DefaultJobsListHref;
    } = DefaultJobsListHref;
}
