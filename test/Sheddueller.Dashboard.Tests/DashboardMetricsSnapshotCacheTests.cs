namespace Sheddueller.Dashboard.Tests;

using Microsoft.Extensions.Time.Testing;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Metrics;

using Shouldly;

public sealed class DashboardMetricsSnapshotCacheTests
{
    [Fact]
    public async Task GetMetricsAsync_RepeatedQueryWithinTtl_UsesCachedSnapshot()
    {
        var reader = new CountingMetricsReader();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero));
        var cache = new DashboardMetricsSnapshotCache(reader, timeProvider);
        var query = new MetricsInspectionQuery([TimeSpan.FromMinutes(5)]);

        var first = await cache.GetMetricsAsync(query);
        var second = await cache.GetMetricsAsync(query);

        reader.CallCount.ShouldBe(1);
        first.Windows[0].QueuedCount.ShouldBe(1);
        second.Windows[0].QueuedCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetMetricsAsync_ExpiredEntry_ReadsAgain()
    {
        var reader = new CountingMetricsReader();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero));
        var cache = new DashboardMetricsSnapshotCache(reader, timeProvider);
        var query = new MetricsInspectionQuery([TimeSpan.FromMinutes(5)]);

        await cache.GetMetricsAsync(query);
        timeProvider.SetUtcNow(timeProvider.GetUtcNow().Add(DashboardMetricsSnapshotCache.TimeToLive));
        var second = await cache.GetMetricsAsync(query);

        reader.CallCount.ShouldBe(2);
        second.Windows[0].QueuedCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetMetricsAsync_ConcurrentSameQuery_CoalescesUnderlyingRead()
    {
        var reader = new BlockingMetricsReader();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero));
        var cache = new DashboardMetricsSnapshotCache(reader, timeProvider);
        var query = new MetricsInspectionQuery([TimeSpan.FromMinutes(5)]);

        var first = cache.GetMetricsAsync(query).AsTask();
        var second = cache.GetMetricsAsync(query).AsTask();
        reader.CallCount.ShouldBe(1);

        reader.Complete();
        var snapshots = await Task.WhenAll(first, second);

        reader.CallCount.ShouldBe(1);
        snapshots[0].Windows[0].QueuedCount.ShouldBe(42);
        snapshots[1].Windows[0].QueuedCount.ShouldBe(42);
    }

    private static MetricsInspectionSnapshot CreateSnapshot(int queuedCount)
      => new([TestData.MetricsWindow(queuedCount)]);

    private sealed class CountingMetricsReader : IMetricsInspectionReader
    {
        public int CallCount { get; private set; }

        public ValueTask<MetricsInspectionSnapshot> GetMetricsAsync(
            MetricsInspectionQuery query,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return ValueTask.FromResult(CreateSnapshot(this.CallCount));
        }
    }

    private sealed class BlockingMetricsReader : IMetricsInspectionReader
    {
        private readonly TaskCompletionSource<MetricsInspectionSnapshot> _completion =
          new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public ValueTask<MetricsInspectionSnapshot> GetMetricsAsync(
            MetricsInspectionQuery query,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return new ValueTask<MetricsInspectionSnapshot>(this._completion.Task);
        }

        public void Complete()
          => this._completion.SetResult(CreateSnapshot(42));
    }
}
