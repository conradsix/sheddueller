namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardClockTests
{
    private static readonly DateTimeOffset StartUtc = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Tick_IntervalElapsed_NotifiesEverySubscriberOnce()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, TimeSpan.FromSeconds(15));
        var firstCount = 0;
        var secondCount = 0;
        clock.Ticked += () => firstCount++;
        clock.Ticked += () => secondCount++;

        timeProvider.Advance(TimeSpan.FromSeconds(14));
        firstCount.ShouldBe(0);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        firstCount.ShouldBe(1);
        secondCount.ShouldBe(1);
        clock.UtcNow.ShouldBe(StartUtc.AddSeconds(15));
    }

    [Fact]
    public void Timer_LastSubscriberLeaves_StopsUntilSomeoneSubscribes()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, TimeSpan.FromSeconds(15));
        var count = 0;
        void Handler() => count++;

        clock.IsTimerRunning.ShouldBeFalse();
        clock.Ticked += Handler;
        clock.IsTimerRunning.ShouldBeTrue();

        clock.Ticked -= Handler;
        clock.IsTimerRunning.ShouldBeFalse();
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        count.ShouldBe(0);

        clock.Ticked += Handler;
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        count.ShouldBe(1);
    }

    [Fact]
    public void Tick_SubscriberThrows_OtherSubscribersStillNotified()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        using var clock = new DashboardClock(timeProvider, TimeSpan.FromSeconds(15));
        var count = 0;
        clock.Ticked += () => throw new InvalidOperationException("render failed");
        clock.Ticked += () => count++;

        timeProvider.Advance(TimeSpan.FromSeconds(15));

        count.ShouldBe(1);
    }

    [Fact]
    public void Dispose_Subscribed_StopsTicking()
    {
        var timeProvider = new FakeTimeProvider(StartUtc);
        var clock = new DashboardClock(timeProvider, TimeSpan.FromSeconds(15));
        var count = 0;
        clock.Ticked += () => count++;

        clock.Dispose();
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        count.ShouldBe(0);
        clock.SubscriberCount.ShouldBe(0);
        clock.IsTimerRunning.ShouldBeFalse();
    }
}
