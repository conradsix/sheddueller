namespace Sheddueller.Dashboard.Tests;

using Microsoft.Extensions.Time.Testing;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardPeriodicRefreshTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Refresh_EachIntervalPasses_RunsOncePerInterval()
    {
        var timeProvider = new FakeTimeProvider();
        var refreshes = 0;
        using var refreshed = new SemaphoreSlim(0);
        await using var periodicRefresh = new DashboardPeriodicRefresh(
          Interval,
          _ =>
          {
              Interlocked.Increment(ref refreshes);
              refreshed.Release();
              return Task.CompletedTask;
          },
          timeProvider);

        timeProvider.Advance(Interval - TimeSpan.FromMilliseconds(1));
        refreshes.ShouldBe(0);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        (await refreshed.WaitAsync(Timeout)).ShouldBeTrue();
        timeProvider.Advance(Interval);
        (await refreshed.WaitAsync(Timeout)).ShouldBeTrue();

        refreshes.ShouldBe(2);
    }

    [Fact]
    public async Task Dispose_RefreshRunning_CancelsItAndWaitsForIt()
    {
        var timeProvider = new FakeTimeProvider();
        var started = new TaskCompletionSource();
        var finished = false;
        var periodicRefresh = new DashboardPeriodicRefresh(
          Interval,
          async cancellationToken =>
          {
              started.TrySetResult();
              try
              {
                  await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
              }
              finally
              {
                  finished = true;
              }
          },
          timeProvider);

        timeProvider.Advance(Interval);
        await started.Task.WaitAsync(Timeout);
        await periodicRefresh.DisposeAsync().AsTask().WaitAsync(Timeout);

        finished.ShouldBeTrue();
    }

    [Fact]
    public async Task Dispose_BeforeTheFirstInterval_NeverRefreshes()
    {
        var timeProvider = new FakeTimeProvider();
        var refreshes = 0;
        var periodicRefresh = new DashboardPeriodicRefresh(
          Interval,
          _ =>
          {
              Interlocked.Increment(ref refreshes);
              return Task.CompletedTask;
          },
          timeProvider);

        await periodicRefresh.DisposeAsync();
        await periodicRefresh.DisposeAsync();
        timeProvider.Advance(Interval * 3);

        refreshes.ShouldBe(0);
    }
}
