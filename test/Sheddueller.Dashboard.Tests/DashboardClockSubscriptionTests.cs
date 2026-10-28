namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardClockSubscriptionTests
{
    private static readonly DateTimeOffset StartUtc = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    [Fact]
    public void Tick_Started_ReadsTheClockAndRendersAgain()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, Tick);
        var renders = 0;
        using var subscription = new DashboardClockSubscription(clock, RunInline, () => renders++);

        subscription.Start();
        timeProvider.Advance(Tick);

        subscription.NowUtc.ShouldBe(StartUtc + Tick);
        renders.ShouldBe(1);
    }

    [Fact]
    public void Tick_NotStarted_LeavesTheTimeAndClockAlone()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, Tick);
        var renders = 0;
        using var subscription = new DashboardClockSubscription(clock, RunInline, () => renders++);

        timeProvider.Advance(Tick);

        subscription.NowUtc.ShouldBe(StartUtc);
        renders.ShouldBe(0);
        clock.SubscriberCount.ShouldBe(0);
    }

    [Fact]
    public void Refresh_NewParameters_ReadsTheClockWithoutRendering()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, Tick);
        var renders = 0;
        using var subscription = new DashboardClockSubscription(clock, RunInline, () => renders++);

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        subscription.Refresh();

        subscription.NowUtc.ShouldBe(StartUtc.AddSeconds(3));
        renders.ShouldBe(0);
    }

    [Fact]
    public void Dispose_Started_StopsFollowingTheClock()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, Tick);
        var renders = 0;
        var subscription = new DashboardClockSubscription(clock, RunInline, () => renders++);
        subscription.Start();
        subscription.Start();
        clock.SubscriberCount.ShouldBe(1);

        subscription.Dispose();
        timeProvider.Advance(Tick);

        clock.SubscriberCount.ShouldBe(0);
        renders.ShouldBe(0);
    }

    [Fact]
    public void Start_AfterDispose_DoesNotSubscribe()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, Tick);
        var subscription = new DashboardClockSubscription(clock, RunInline, () => { });

        subscription.Dispose();
        subscription.Start();

        clock.SubscriberCount.ShouldBe(0);
    }

    [Fact]
    public void Tick_DisposedBeforeTheRendererRuns_DoesNotRender()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, Tick);
        var renders = 0;
        Action? queued = null;
        var subscription = new DashboardClockSubscription(
          clock,
          action =>
          {
              queued = action;
              return Task.CompletedTask;
          },
          () => renders++);
        subscription.Start();

        timeProvider.Advance(Tick);
        subscription.Dispose();
        queued.ShouldNotBeNull().Invoke();

        renders.ShouldBe(0);
        subscription.NowUtc.ShouldBe(StartUtc);
    }

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
