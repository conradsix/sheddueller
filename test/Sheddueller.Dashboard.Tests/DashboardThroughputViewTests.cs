namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardThroughputViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 26, 12, 0, 5, TimeSpan.Zero);

    [Fact]
    public void ThroughputView_FiveMinuteWindow_ShowsNewestFiveSecondBuckets()
    {
        var view = DashboardThroughputView.Create(CreateSnapshot(), TimeSpan.FromMinutes(5));

        view.BucketSize.ShouldBe(TimeSpan.FromSeconds(5));
        view.Span.ShouldBe(TimeSpan.FromMinutes(5));
        view.CoversWindow.ShouldBeTrue();
        view.Buckets.Count.ShouldBe(60);
        view.WindowStartUtc.ShouldBe(Now.AddSeconds(-295));
        view.WindowEndUtc.ShouldBe(Now);
        view.Caption.ShouldBe("Job events per 5 seconds over the last 5 minutes");
        view.CoverageNote.ShouldBeNull();
    }

    [Fact]
    public void ThroughputView_HourWindow_MergesIntoAlignedThirtySecondBuckets()
    {
        var view = DashboardThroughputView.Create(CreateSnapshot(queued: static _ => 1), TimeSpan.FromHours(1));

        view.BucketSize.ShouldBe(TimeSpan.FromSeconds(30));
        view.Buckets.Count.ShouldBeLessThanOrEqualTo(DashboardThroughputView.MaximumPoints);
        view.Buckets.ShouldAllBe(bucket => bucket.StartedAtUtc.ToUnixTimeSeconds() % 30 == 0);

        // The oldest 30-second group is only partly inside the hour, so it is dropped; the newest is still filling.
        view.WindowStartUtc.ShouldBe(new DateTimeOffset(2026, 4, 26, 11, 0, 30, TimeSpan.Zero));
        view.WindowEndUtc.ShouldBe(new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero));
        view.Buckets[0].QueuedCount.ShouldBe(6);
        view.Buckets[^1].QueuedCount.ShouldBe(2);
        view.Caption.ShouldBe("Job events per 30 seconds over the last hour");
    }

    [Fact]
    public void ThroughputView_WindowLongerThanHistory_ShowsAllHistoryWithNote()
    {
        var snapshot = CreateSnapshot();

        var view = DashboardThroughputView.Create(snapshot, TimeSpan.FromHours(24));

        view.Span.ShouldBe(TimeSpan.FromHours(1));
        view.CoversWindow.ShouldBeFalse();
        view.CoverageNote.ShouldBe("This chart keeps the last hour only.");
        view.Buckets.ShouldBe(DashboardThroughputView.Create(snapshot, TimeSpan.FromHours(1)).Buckets);
    }

    [Fact]
    public void ThroughputView_Totals_CountOnlyShownBuckets()
    {
        var snapshot = CreateSnapshot(failed: static startedAtUtc => startedAtUtc == Now || startedAtUtc == Now.AddMinutes(-30) ? 3 : 0);

        var fiveMinutes = DashboardThroughputView.Create(snapshot, TimeSpan.FromMinutes(5));
        var hour = DashboardThroughputView.Create(snapshot, TimeSpan.FromHours(1));

        fiveMinutes.Totals.FailedCount.ShouldBe(3);
        hour.Totals.FailedCount.ShouldBe(6);
        fiveMinutes.HasActivity.ShouldBeTrue();
    }

    [Fact]
    public void ThroughputView_NoEvents_HasNoActivity()
    {
        var view = DashboardThroughputView.Create(CreateSnapshot(), TimeSpan.FromHours(1));

        view.HasActivity.ShouldBeFalse();
        view.Totals.QueuedCount.ShouldBe(0);
    }

    // Mirrors DashboardThroughputStore: one hour of five-second buckets, the newest starting at Now.
    private static DashboardThroughputSnapshot CreateSnapshot(
        Func<DateTimeOffset, int>? queued = null,
        Func<DateTimeOffset, int>? failed = null)
    {
        const int bucketCount = 720;
        var windowStartUtc = Now.AddSeconds(-(bucketCount - 1) * 5);
        var buckets = Enumerable.Range(0, bucketCount)
          .Select(index => windowStartUtc.AddSeconds(index * 5))
          .Select(startedAtUtc => new DashboardThroughputBucket(
            startedAtUtc,
            queued?.Invoke(startedAtUtc) ?? 0,
            StartedCount: 0,
            SucceededCount: 0,
            failed?.Invoke(startedAtUtc) ?? 0,
            CanceledCount: 0,
            FailedAttemptCount: 0))
          .ToArray();

        return new DashboardThroughputSnapshot(windowStartUtc, Now, TimeSpan.FromSeconds(5), buckets);
    }
}
