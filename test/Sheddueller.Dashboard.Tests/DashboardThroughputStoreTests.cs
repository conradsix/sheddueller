namespace Sheddueller.Dashboard.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardThroughputStoreTests
{
    [Fact]
    public void Snapshot_NoEvents_ZeroFillsOneHourOfFiveSecondBuckets()
    {
        var now = new DateTimeOffset(2026, 4, 26, 12, 0, 5, TimeSpan.Zero);
        using var store = CreateStore(now);

        var snapshot = store.GetSnapshot();

        snapshot.BucketSize.ShouldBe(TimeSpan.FromSeconds(5));
        snapshot.Buckets.Count.ShouldBe(720);
        snapshot.WindowStartUtc.ShouldBe(now.AddSeconds(-3595));
        snapshot.WindowEndUtc.ShouldBe(now);
        snapshot.Buckets[0].StartedAtUtc.ShouldBe(snapshot.WindowStartUtc);
        snapshot.Buckets[^1].StartedAtUtc.ShouldBe(now);
        snapshot.Buckets.Sum(bucket => bucket.QueuedCount).ShouldBe(0);
    }

    [Fact]
    public void Record_KnownJobEvents_StoresCountsInFiveSecondBucket()
    {
        var now = new DateTimeOffset(2026, 4, 26, 12, 0, 5, TimeSpan.Zero);
        using var store = CreateStore(now);

        store.Record(CreateEvent(JobEventKind.Lifecycle, now, "Queued"));
        store.Record(CreateEvent(JobEventKind.AttemptStarted, now));
        store.Record(CreateEvent(JobEventKind.AttemptCompleted, now));
        store.Record(CreateEvent(JobEventKind.AttemptFailed, now));
        store.Record(CreateEvent(JobEventKind.Lifecycle, now, "Failed"));
        store.Record(CreateEvent(JobEventKind.Lifecycle, now, "Canceled"));
        store.Record(CreateEvent(JobEventKind.Log, now, "ignored"));

        var bucket = store.GetSnapshot().Buckets.Single(bucket => bucket.StartedAtUtc == now);
        bucket.QueuedCount.ShouldBe(1);
        bucket.StartedCount.ShouldBe(1);
        bucket.SucceededCount.ShouldBe(1);
        bucket.FailedAttemptCount.ShouldBe(1);
        bucket.FailedCount.ShouldBe(1);
        bucket.CanceledCount.ShouldBe(1);
    }

    [Fact]
    public void Record_OutsideRollingWindow_IgnoresEvent()
    {
        var now = new DateTimeOffset(2026, 4, 26, 12, 0, 5, TimeSpan.Zero);
        using var store = CreateStore(now);

        store.Record(CreateEvent(JobEventKind.Lifecycle, now.AddHours(-2), "Queued"));
        store.Record(CreateEvent(JobEventKind.Lifecycle, now.AddSeconds(1), "Queued"));

        store.GetSnapshot().Buckets.Sum(bucket => bucket.QueuedCount).ShouldBe(0);
    }

    [Fact]
    public void Record_ReusedSlot_ResetsPreviousBucketCounts()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero));
        using var store = new DashboardThroughputStore(new DashboardLiveUpdateStream(NullLogger<DashboardLiveUpdateStream>.Instance), timeProvider);

        store.Record(CreateEvent(JobEventKind.Lifecycle, timeProvider.GetUtcNow(), "Queued"));

        timeProvider.SetUtcNow(timeProvider.GetUtcNow().AddHours(1));
        store.Record(CreateEvent(JobEventKind.AttemptStarted, timeProvider.GetUtcNow()));

        var bucket = store.GetSnapshot().Buckets.Single(bucket => bucket.StartedAtUtc == timeProvider.GetUtcNow());
        bucket.QueuedCount.ShouldBe(0);
        bucket.StartedCount.ShouldBe(1);
    }

    private static DashboardThroughputStore CreateStore(DateTimeOffset now)
      => new(new DashboardLiveUpdateStream(NullLogger<DashboardLiveUpdateStream>.Instance), new FakeTimeProvider(now));

    private static JobEvent CreateEvent(
        JobEventKind kind,
        DateTimeOffset occurredAtUtc,
        string? message = null)
      => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        EventSequence: 1,
        kind,
        occurredAtUtc,
        AttemptNumber: 1,
        Message: message);
}
