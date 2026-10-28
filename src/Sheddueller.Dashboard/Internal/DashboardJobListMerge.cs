namespace Sheddueller.Dashboard.Internal;

using Sheddueller.Inspection.Jobs;

/// <summary>
/// Merges live query results into the jobs list without moving rows under the user's cursor.
/// </summary>
internal static class DashboardJobListMerge
{
    /// <summary>
    /// Updates displayed rows in place from <paramref name="fresh"/>, keeping their order. Rows that are not in the
    /// fresh result (they no longer match, or fell out of the loaded window) are kept unchanged. Fresh rows that are
    /// not displayed are held back and reported as <see cref="DashboardJobListMergeResult.NewJobIds"/>. When nothing is
    /// displayed yet there is nothing to disturb, so the fresh result is shown as-is.
    /// </summary>
    public static DashboardJobListMergeResult Merge(
        IReadOnlyList<JobInspectionSummary> displayed,
        IReadOnlyList<JobInspectionSummary> fresh)
    {
        ArgumentNullException.ThrowIfNull(displayed);
        ArgumentNullException.ThrowIfNull(fresh);

        if (displayed.Count == 0)
        {
            return new DashboardJobListMergeResult([.. DistinctById(fresh)], []);
        }

        var freshById = new Dictionary<Guid, JobInspectionSummary>(fresh.Count);
        foreach (var job in fresh)
        {
            _ = freshById.TryAdd(job.JobId, job);
        }

        var displayedIds = new HashSet<Guid>(displayed.Count);
        var rows = new List<JobInspectionSummary>(displayed.Count);
        foreach (var job in displayed)
        {
            if (!displayedIds.Add(job.JobId))
            {
                continue;
            }

            rows.Add(freshById.TryGetValue(job.JobId, out var updated) ? updated : job);
        }

        var newJobIds = new List<Guid>();
        var seenNew = new HashSet<Guid>();
        foreach (var job in fresh)
        {
            if (!displayedIds.Contains(job.JobId) && seenNew.Add(job.JobId))
            {
                newJobIds.Add(job.JobId);
            }
        }

        return new DashboardJobListMergeResult(rows, newJobIds);
    }

    /// <summary>
    /// Appends a further page to the displayed rows, skipping jobs that are already displayed (offset-based
    /// continuation can repeat rows when new jobs arrive ahead of the loaded window).
    /// </summary>
    public static IReadOnlyList<JobInspectionSummary> Append(
        IReadOnlyList<JobInspectionSummary> displayed,
        IReadOnlyList<JobInspectionSummary> page)
    {
        ArgumentNullException.ThrowIfNull(displayed);
        ArgumentNullException.ThrowIfNull(page);

        var ids = new HashSet<Guid>(displayed.Select(static job => job.JobId));
        var rows = new List<JobInspectionSummary>(displayed.Count + page.Count);
        rows.AddRange(displayed);
        foreach (var job in page)
        {
            if (ids.Add(job.JobId))
            {
                rows.Add(job);
            }
        }

        return rows;
    }

    private static IEnumerable<JobInspectionSummary> DistinctById(IEnumerable<JobInspectionSummary> jobs)
    {
        var ids = new HashSet<Guid>();
        foreach (var job in jobs)
        {
            if (ids.Add(job.JobId))
            {
                yield return job;
            }
        }
    }
}

internal sealed record DashboardJobListMergeResult(
    IReadOnlyList<JobInspectionSummary> Rows,
    IReadOnlyList<Guid> NewJobIds)
{
    public int NewJobCount
      => this.NewJobIds.Count;
}
