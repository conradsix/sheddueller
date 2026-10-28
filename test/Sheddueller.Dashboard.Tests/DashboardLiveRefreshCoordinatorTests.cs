namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardLiveRefreshCoordinatorTests
{
    [Fact]
    public async Task QueueRefresh_MultipleEventsWithinThrottle_RunsOneRefresh()
    {
        var refreshCount = 0;
        var delay = new ControlledDelay();
        var throttle = TimeSpan.FromSeconds(1);
        using var coordinator = new DashboardLiveRefreshCoordinator(
          _ =>
          {
              refreshCount++;
              return Task.CompletedTask;
          },
          action =>
          {
              action();
              return Task.CompletedTask;
          },
          throttle: throttle,
          delayAsync: delay.DelayAsync);

        coordinator.QueueRefresh();
        coordinator.QueueRefresh();

        var firstDelay = await delay.TakeAsync();
        firstDelay.Delay.ShouldBe(throttle);
        refreshCount.ShouldBe(0);

        firstDelay.Complete();
        var drainDelay = await delay.TakeAsync();

        refreshCount.ShouldBe(1);
        coordinator.RefreshError.ShouldBeNull();
        coordinator.LastUpdatedUtc.ShouldNotBeNull();

        drainDelay.Complete();
    }

    [Fact]
    public async Task RefreshNow_FailureThenSuccess_SurfacesErrorAndRecovers()
    {
        var shouldFail = true;
        using var coordinator = new DashboardLiveRefreshCoordinator(
          _ =>
          {
              if (shouldFail)
              {
                  throw new InvalidOperationException("read failed");
              }

              return Task.CompletedTask;
          },
          action =>
          {
              action();
              return Task.CompletedTask;
          });

        await coordinator.RefreshNowAsync();

        coordinator.RefreshError.ShouldBe("read failed");
        coordinator.IsRefreshing.ShouldBeFalse();

        shouldFail = false;
        await coordinator.RefreshNowAsync();

        coordinator.RefreshError.ShouldBeNull();
        coordinator.LastUpdatedUtc.ShouldNotBeNull();
        coordinator.IsRefreshing.ShouldBeFalse();
    }

    [Fact]
    public async Task MarkLiveActivity_ExistingRefreshError_PreservesErrorForInlineAlert()
    {
        using var coordinator = new DashboardLiveRefreshCoordinator(
          _ => throw new InvalidOperationException("read failed"),
          action =>
          {
              action();
              return Task.CompletedTask;
          });

        await coordinator.RefreshNowAsync();
        coordinator.RefreshError.ShouldBe("read failed");

        var liveEventAt = DateTimeOffset.Parse("2026-04-20T12:08:00Z", CultureInfo.InvariantCulture);
        coordinator.MarkLiveActivity(liveEventAt);

        coordinator.LastUpdatedUtc.ShouldBe(liveEventAt);
        coordinator.RefreshError.ShouldBe("read failed");
        coordinator.IsRefreshing.ShouldBeFalse();
    }

    [Fact]
    public async Task LiveEvents_WithinThrottle_RenderOnlyThroughOneRefresh()
    {
        var refreshCount = 0;
        var applyCount = 0;
        var delay = new ControlledDelay();
        using var coordinator = new DashboardLiveRefreshCoordinator(
          _ =>
          {
              refreshCount++;
              return Task.CompletedTask;
          },
          action =>
          {
              applyCount++;
              action();
              return Task.CompletedTask;
          },
          throttle: TimeSpan.FromSeconds(1),
          delayAsync: delay.DelayAsync);
        var firstEventAt = DateTimeOffset.Parse("2026-04-20T12:08:00Z", CultureInfo.InvariantCulture);
        var lastEventAt = firstEventAt.AddMilliseconds(400);

        coordinator.QueueLiveRefresh(firstEventAt);
        coordinator.QueueLiveRefresh(firstEventAt.AddMilliseconds(200));
        coordinator.QueueLiveRefresh(lastEventAt);

        var throttleDelay = await delay.TakeAsync();
        applyCount.ShouldBe(0);
        refreshCount.ShouldBe(0);
        coordinator.LastUpdatedUtc.ShouldBe(lastEventAt);

        throttleDelay.Complete();
        var drainDelay = await delay.TakeAsync();

        refreshCount.ShouldBe(1);

        drainDelay.Complete();
    }

    [Fact]
    public void LiveEvent_AutoRefreshPaused_DoesNotMarkActivityOrQueueRefresh()
    {
        var refreshCount = 0;
        var applyCount = 0;
        var delay = new ControlledDelay();
        using var coordinator = new DashboardLiveRefreshCoordinator(
          _ =>
          {
              refreshCount++;
              return Task.CompletedTask;
          },
          action =>
          {
              applyCount++;
              action();
              return Task.CompletedTask;
          },
          autoRefreshEnabled: false,
          throttle: TimeSpan.FromSeconds(1),
          delayAsync: delay.DelayAsync);

        coordinator.QueueLiveRefresh(DateTimeOffset.Parse("2026-04-20T12:08:00Z", CultureInfo.InvariantCulture));

        coordinator.LastUpdatedUtc.ShouldBeNull();
        delay.PendingCount.ShouldBe(0);
        refreshCount.ShouldBe(0);
        applyCount.ShouldBe(0);
    }

    [Fact]
    public void LiveEvent_OlderThanLastUpdate_KeepsLastUpdatedTime()
    {
        var delay = new ControlledDelay();
        using var coordinator = new DashboardLiveRefreshCoordinator(
          _ => Task.CompletedTask,
          action =>
          {
              action();
              return Task.CompletedTask;
          },
          throttle: TimeSpan.FromSeconds(1),
          delayAsync: delay.DelayAsync);
        var refreshedAt = DateTimeOffset.Parse("2026-04-20T12:08:05Z", CultureInfo.InvariantCulture);
        coordinator.MarkUpdated(refreshedAt);

        coordinator.QueueLiveRefresh(refreshedAt.AddSeconds(-3));

        coordinator.LastUpdatedUtc.ShouldBe(refreshedAt);
        delay.PendingCount.ShouldBe(1);
    }

    [Fact]
    public async Task SetAutoRefreshEnabled_Disabled_SuppressesQueuedRefreshes()
    {
        var refreshCount = 0;
        var delay = new ControlledDelay();
        using var coordinator = new DashboardLiveRefreshCoordinator(
          _ =>
          {
              refreshCount++;
              return Task.CompletedTask;
          },
          action =>
          {
              action();
              return Task.CompletedTask;
          },
          throttle: TimeSpan.FromSeconds(1),
          delayAsync: delay.DelayAsync);

        await coordinator.SetAutoRefreshEnabledAsync(enabled: false);
        coordinator.QueueRefresh();

        refreshCount.ShouldBe(0);
        delay.PendingCount.ShouldBe(0);
        coordinator.AutoRefreshEnabled.ShouldBeFalse();
    }

    [Fact]
    public void ShellRefreshContext_RegisterAndDispose_TracksCurrentPage()
    {
        using var first = CreateCoordinator();
        using var second = CreateCoordinator();
        var context = new DashboardShellRefreshContext();
        var changedCount = 0;
        context.Changed += () => changedCount++;

        var firstRegistration = context.Register(first, CreateShellOptions());
        var secondRegistration = context.Register(second, CreateShellOptions());

        context.Current.ShouldNotBeNull();
        context.Current.Coordinator.ShouldBe(second);
        changedCount.ShouldBe(2);

        firstRegistration.Dispose();
        context.Current.ShouldNotBeNull();
        context.Current.Coordinator.ShouldBe(second);
        changedCount.ShouldBe(2);

        secondRegistration.Dispose();
        context.Current.ShouldBeNull();
        changedCount.ShouldBe(3);
    }

    [Fact]
    public async Task ShellRefreshContext_ToggleCurrentAutoRefresh_UpdatesCurrentCoordinator()
    {
        using var coordinator = CreateCoordinator();
        var context = new DashboardShellRefreshContext();
        var changedCount = 0;
        context.Changed += () => changedCount++;

        using var registration = context.Register(coordinator, CreateShellOptions());
        await context.ToggleCurrentAutoRefreshAsync();

        coordinator.AutoRefreshEnabled.ShouldBeFalse();
        changedCount.ShouldBe(2);
    }

    private static DashboardLiveRefreshCoordinator CreateCoordinator()
      => new(
        _ => Task.CompletedTask,
        action =>
        {
            action();
            return Task.CompletedTask;
        });

    private static DashboardShellRefreshOptions CreateShellOptions()
      => new(
        ShowRefreshing: true,
        LastUpdatedUtc: _ => null);

    private sealed class ControlledDelay
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private readonly Lock _gate = new();
        private readonly Queue<DelayRequest> _requests = [];
        private readonly Queue<TaskCompletionSource<DelayRequest>> _waiters = [];

        public int PendingCount
        {
            get
            {
                lock (this._gate)
                {
                    return this._requests.Count;
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var request = new DelayRequest(delay);
            TaskCompletionSource<DelayRequest>? waiter = null;

            lock (this._gate)
            {
                if (this._waiters.Count == 0)
                {
                    this._requests.Enqueue(request);
                }
                else
                {
                    waiter = this._waiters.Dequeue();
                }
            }

            waiter?.SetResult(request);
            return request.Task.WaitAsync(cancellationToken);
        }

        public Task<DelayRequest> TakeAsync()
        {
            lock (this._gate)
            {
                if (this._requests.Count > 0)
                {
                    return Task.FromResult(this._requests.Dequeue());
                }

                var waiter = new TaskCompletionSource<DelayRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
                this._waiters.Enqueue(waiter);
                return waiter.Task.WaitAsync(Timeout);
            }
        }
    }

    private sealed class DelayRequest(TimeSpan delay)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Delay { get; } = delay;

        public Task Task => this._completion.Task;

        public void Complete()
          => this._completion.SetResult();
    }
}
