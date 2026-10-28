namespace Sheddueller.Dashboard.Internal;

/// <summary>
/// The slice of the live throughput history that the metrics chart shows for a window: the newest
/// <see cref="Span"/> of buckets, merged into wider buckets so the chart keeps a readable number of points.
/// </summary>
/// <param name="Span">How much history the chart covers: the window, or all retained history when that is shorter.</param>
/// <param name="BucketSize">Width of each (possibly merged) bucket.</param>
/// <param name="CoversWindow">Whether the retained history covers the whole window.</param>
/// <param name="Buckets">The buckets in time order; the newest one may still be filling.</param>
internal sealed record DashboardThroughputView(
    TimeSpan Span,
    TimeSpan BucketSize,
    bool CoversWindow,
    IReadOnlyList<DashboardThroughputBucket> Buckets)
{
    /// <summary>
    /// The most points a series gets; longer spans merge neighbouring buckets.
    /// </summary>
    public const int MaximumPoints = 120;

    public DateTimeOffset? WindowStartUtc
      => this.Buckets.Count == 0 ? null : this.Buckets[0].StartedAtUtc;

    public DateTimeOffset? WindowEndUtc
      => this.Buckets.Count == 0 ? null : this.Buckets[^1].StartedAtUtc;

    public DashboardThroughputBucket Totals
      => new(
        this.WindowStartUtc ?? default,
        this.Buckets.Sum(static bucket => bucket.QueuedCount),
        this.Buckets.Sum(static bucket => bucket.StartedCount),
        this.Buckets.Sum(static bucket => bucket.SucceededCount),
        this.Buckets.Sum(static bucket => bucket.FailedCount),
        this.Buckets.Sum(static bucket => bucket.CanceledCount),
        this.Buckets.Sum(static bucket => bucket.FailedAttemptCount));

    public bool HasActivity
      => this.Buckets.Any(static bucket =>
        bucket.QueuedCount > 0
        || bucket.StartedCount > 0
        || bucket.SucceededCount > 0
        || bucket.FailedCount > 0
        || bucket.CanceledCount > 0
        || bucket.FailedAttemptCount > 0);

    /// <summary>
    /// What the chart plots, for example "Job events per 30 seconds over the last hour".
    /// </summary>
    public string Caption
      => string.Concat(
        "Job events per ",
        DashboardMetricsFormat.Period(this.BucketSize),
        " over the last ",
        DashboardMetricsFormat.Period(this.Span));

    /// <summary>
    /// Explains a chart that is shorter than the selected window, or <see langword="null"/> when it covers it.
    /// </summary>
    public string? CoverageNote
      => this.CoversWindow
        ? null
        : string.Concat("This chart keeps the last ", DashboardMetricsFormat.Period(this.Span), " only.");

    public static DashboardThroughputView Create(
        DashboardThroughputSnapshot snapshot,
        TimeSpan window)
    {
        var sourceSize = snapshot.BucketSize > TimeSpan.Zero ? snapshot.BucketSize : TimeSpan.FromSeconds(5);
        var retained = sourceSize * snapshot.Buckets.Count;
        var span = window > TimeSpan.Zero && window < retained ? window : retained;
        var takeCount = (int)Math.Clamp(Math.Ceiling(span / sourceSize), 0, snapshot.Buckets.Count);
        var recent = snapshot.Buckets.Skip(snapshot.Buckets.Count - takeCount).ToArray();
        var factor = Math.Max(1, (int)Math.Ceiling(takeCount / (double)MaximumPoints));

        return new DashboardThroughputView(
          span,
          sourceSize * factor,
          window <= retained,
          factor == 1 ? recent : Merge(recent, sourceSize * factor));
    }

    // Merged buckets line up with whole multiples of their size, so a bucket keeps its counts as time moves on. The
    // oldest group is dropped when part of it is outside the span; the newest one is the bucket still filling.
    private static DashboardThroughputBucket[] Merge(
        IReadOnlyList<DashboardThroughputBucket> buckets,
        TimeSpan size)
    {
        var sizeSeconds = (long)size.TotalSeconds;
        return
        [
            .. buckets
              .GroupBy(bucket => Math.Floor(bucket.StartedAtUtc.ToUnixTimeSeconds() / (double)sizeSeconds))
              .Select(group => (Start: DateTimeOffset.FromUnixTimeSeconds((long)group.Key * sizeSeconds), Buckets: group.ToArray()))
              .Where((group, index) => index > 0 || group.Buckets[0].StartedAtUtc == group.Start)
              .Select(group => new DashboardThroughputBucket(
                group.Start,
                group.Buckets.Sum(static bucket => bucket.QueuedCount),
                group.Buckets.Sum(static bucket => bucket.StartedCount),
                group.Buckets.Sum(static bucket => bucket.SucceededCount),
                group.Buckets.Sum(static bucket => bucket.FailedCount),
                group.Buckets.Sum(static bucket => bucket.CanceledCount),
                group.Buckets.Sum(static bucket => bucket.FailedAttemptCount))),
        ];
    }
}
