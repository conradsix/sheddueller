namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardShellRefreshContextTests
{
    [Fact]
    public async Task AutoRefreshPreference_PausedThenNavigated_NextPageStartsPaused()
    {
        var context = new DashboardShellRefreshContext();
        using var firstPage = TestData.RefreshCoordinator();
        var firstRegistration = context.Register(firstPage, TestData.ShellRefreshOptions());

        await context.ToggleCurrentAutoRefreshAsync();
        firstRegistration.Dispose();

        using var secondPage = TestData.RefreshCoordinator();
        using var secondRegistration = context.Register(secondPage, TestData.ShellRefreshOptions());

        firstPage.AutoRefreshEnabled.ShouldBeFalse();
        secondPage.AutoRefreshEnabled.ShouldBeFalse();
        context.AutoRefreshEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task AutoRefreshPreference_ResumedThenNavigated_NextPageStartsLive()
    {
        var context = new DashboardShellRefreshContext();
        using var firstPage = TestData.RefreshCoordinator();
        var firstRegistration = context.Register(firstPage, TestData.ShellRefreshOptions());

        await context.ToggleCurrentAutoRefreshAsync();
        await context.ToggleCurrentAutoRefreshAsync();
        firstRegistration.Dispose();

        using var secondPage = TestData.RefreshCoordinator(autoRefreshEnabled: false);
        using var secondRegistration = context.Register(secondPage, TestData.ShellRefreshOptions());

        secondPage.AutoRefreshEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task AutoRefreshPreference_ToggledWithoutRegisteredPage_IsRemembered()
    {
        var context = new DashboardShellRefreshContext();
        var changedCount = 0;
        context.Changed += () => changedCount++;

        await context.ToggleCurrentAutoRefreshAsync();

        context.AutoRefreshEnabled.ShouldBeFalse();
        changedCount.ShouldBe(1);

        using var page = TestData.RefreshCoordinator();
        using var registration = context.Register(page, TestData.ShellRefreshOptions());
        page.AutoRefreshEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task AutoRefreshPreference_RegisteredWhilePaused_DoesNotRefresh()
    {
        var context = new DashboardShellRefreshContext();
        await context.ToggleCurrentAutoRefreshAsync();
        var refreshCount = 0;
        using var page = TestData.RefreshCoordinator(onRefresh: () => refreshCount++);

        using var registration = context.Register(page, TestData.ShellRefreshOptions());
        page.QueueRefresh();

        refreshCount.ShouldBe(0);
    }

    [Fact]
    public async Task RefreshCurrent_PausedPage_RefreshesOnceAndStaysPaused()
    {
        var context = new DashboardShellRefreshContext();
        var refreshCount = 0;
        using var page = TestData.RefreshCoordinator(onRefresh: () => refreshCount++);
        using var registration = context.Register(page, TestData.ShellRefreshOptions());
        await context.ToggleCurrentAutoRefreshAsync();
        var changedCount = 0;
        context.Changed += () => changedCount++;

        await context.RefreshCurrentAsync();

        refreshCount.ShouldBe(1);
        page.AutoRefreshEnabled.ShouldBeFalse();
        page.LastUpdatedUtc.ShouldNotBeNull();
        changedCount.ShouldBe(1);
    }

    [Fact]
    public async Task RefreshCurrent_NoRegisteredPage_DoesNothing()
    {
        var context = new DashboardShellRefreshContext();
        var changedCount = 0;
        context.Changed += () => changedCount++;

        await context.RefreshCurrentAsync();

        changedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Coordinator_QueuedAndExplicitRefresh_UseSeparateDelegates()
    {
        var explicitCount = 0;
        var queuedCount = 0;
        var queuedRefreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new DashboardLiveRefreshCoordinator(
          _ =>
          {
              explicitCount++;
              return Task.CompletedTask;
          },
          action =>
          {
              action();
              return Task.CompletedTask;
          },
          throttle: TimeSpan.Zero,
          delayAsync: (_, _) => Task.CompletedTask,
          queuedRefreshAsync: _ =>
          {
              queuedCount++;
              queuedRefreshed.TrySetResult();
              return Task.CompletedTask;
          });

        await coordinator.RefreshNowAsync();
        coordinator.QueueRefresh();
        await queuedRefreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        explicitCount.ShouldBe(1);
        queuedCount.ShouldBe(1);
    }
}
