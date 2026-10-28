namespace Sheddueller.Dashboard.Tests;

using Microsoft.Extensions.Time.Testing;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardDebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Schedule_DelayPasses_RunsTheActionOnce()
    {
        var timeProvider = new FakeTimeProvider();
        using var debouncer = new DashboardDebouncer(Delay, timeProvider);
        var runs = 0;
        var ran = new TaskCompletionSource();

        debouncer.Schedule(() =>
        {
            Interlocked.Increment(ref runs);
            ran.TrySetResult();
            return Task.CompletedTask;
        });

        debouncer.IsPending.ShouldBeTrue();
        timeProvider.Advance(Delay - TimeSpan.FromMilliseconds(1));
        runs.ShouldBe(0);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await ran.Task.WaitAsync(Timeout);
        runs.ShouldBe(1);
    }

    [Fact]
    public async Task Schedule_AgainBeforeDelay_RunsOnlyTheLastActionAfterItsOwnDelay()
    {
        var timeProvider = new FakeTimeProvider();
        using var debouncer = new DashboardDebouncer(Delay, timeProvider);
        var firstRuns = 0;
        var secondRuns = 0;
        var secondRan = new TaskCompletionSource();

        debouncer.Schedule(() =>
        {
            Interlocked.Increment(ref firstRuns);
            return Task.CompletedTask;
        });
        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        debouncer.Schedule(() =>
        {
            Interlocked.Increment(ref secondRuns);
            secondRan.TrySetResult();
            return Task.CompletedTask;
        });

        // Past the first action's time, but not the second's.
        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        secondRuns.ShouldBe(0);

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await secondRan.Task.WaitAsync(Timeout);
        secondRuns.ShouldBe(1);
        firstRuns.ShouldBe(0);
    }

    [Fact]
    public void Cancel_BeforeDelay_ActionNeverRuns()
    {
        var timeProvider = new FakeTimeProvider();
        using var debouncer = new DashboardDebouncer(Delay, timeProvider);
        var runs = 0;

        debouncer.Schedule(() =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });
        debouncer.Cancel();
        timeProvider.Advance(Delay * 2);

        debouncer.IsPending.ShouldBeFalse();
        runs.ShouldBe(0);
    }

    [Fact]
    public void Dispose_PendingAction_NeverRuns()
    {
        var timeProvider = new FakeTimeProvider();
        var debouncer = new DashboardDebouncer(Delay, timeProvider);
        var runs = 0;

        debouncer.Schedule(() =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });
        debouncer.Dispose();
        timeProvider.Advance(Delay * 2);

        runs.ShouldBe(0);
    }
}
