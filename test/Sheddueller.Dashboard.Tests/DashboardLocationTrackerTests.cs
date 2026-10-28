namespace Sheddueller.Dashboard.Tests;

using Microsoft.AspNetCore.Components;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardLocationTrackerTests
{
    private const string RootUri = "http://localhost/sheddueller/";

    [Fact]
    public void OwnNavigation_LocationChanged_IsNotReportedAsExternal()
    {
        var navigation = new TestNavigationManager("schedules");
        var external = new List<string>();
        using var tracker = new DashboardLocationTracker(navigation, "schedules", external.Add);

        tracker.NavigateTo("schedules?state=paused", replace: false).ShouldBeTrue();
        navigation.Complete("schedules?state=paused");

        external.ShouldBeEmpty();
    }

    [Fact]
    public void OwnNavigation_RapidChangesCompleteLater_NoneReportedAsExternal()
    {
        var navigation = new TestNavigationManager("jobs");
        var external = new List<string>();
        using var tracker = new DashboardLocationTracker(navigation, "jobs", external.Add);

        tracker.NavigateTo("jobs?state=Failed", replace: false);
        tracker.NavigateTo("jobs?state=Failed&state=Queued", replace: false);
        navigation.Complete("jobs?state=Failed");
        navigation.Complete("jobs?state=Failed&state=Queued");

        external.ShouldBeEmpty();
        navigation.Navigations.Count.ShouldBe(2);
    }

    [Fact]
    public void BackNavigation_SamePage_IsReportedAsExternal()
    {
        var navigation = new TestNavigationManager("jobs?state=Failed");
        var external = new List<string>();
        using var tracker = new DashboardLocationTracker(navigation, "jobs", external.Add);

        navigation.Complete("jobs");

        external.ShouldBe([RootUri + "jobs"]);
    }

    [Fact]
    public void LocationChange_OtherPage_IsIgnored()
    {
        var navigation = new TestNavigationManager("jobs");
        var external = new List<string>();
        using var tracker = new DashboardLocationTracker(navigation, "jobs", external.Add);

        navigation.Complete("jobs/8c32d457-9e7a-42bb-8947-0c8fa54743be");

        external.ShouldBeEmpty();
    }

    [Fact]
    public void NavigateTo_CurrentOrPendingLocation_DoesNotAddHistory()
    {
        var navigation = new TestNavigationManager("nodes?state=Stale");
        using var tracker = new DashboardLocationTracker(navigation, "nodes", _ => { });

        tracker.NavigateTo("nodes?state=Stale", replace: false).ShouldBeFalse();
        tracker.NavigateTo("nodes?state=Dead", replace: false).ShouldBeTrue();
        tracker.NavigateTo("nodes?state=Dead", replace: false).ShouldBeFalse();

        navigation.Navigations.ShouldBe([("nodes?state=Dead", false)]);
    }

    [Fact]
    public void NavigateTo_Typing_ReplacesHistoryEntry()
    {
        var navigation = new TestNavigationManager("nodes");
        using var tracker = new DashboardLocationTracker(navigation, "nodes", _ => { });

        tracker.NavigateTo("nodes?node=eu", replace: true);

        navigation.Navigations.ShouldBe([("nodes?node=eu", true)]);
    }

    [Fact]
    public void Dispose_LaterLocationChange_IsNotReported()
    {
        var navigation = new TestNavigationManager("jobs?state=Failed");
        var external = new List<string>();
        var tracker = new DashboardLocationTracker(navigation, "jobs", external.Add);

        tracker.Dispose();
        navigation.Complete("jobs");

        external.ShouldBeEmpty();
        tracker.NavigateTo("jobs?state=Queued", replace: false).ShouldBeFalse();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string relativeUri)
          => this.Initialize(RootUri, RootUri + relativeUri);

        public List<(string Uri, bool Replace)> Navigations { get; } = [];

        public void Complete(string relativeUri)
        {
            this.Uri = RootUri + relativeUri;
            this.NotifyLocationChanged(isInterceptedLink: false);
        }

        protected override void NavigateToCore(
            string uri,
            NavigationOptions options)
          => this.Navigations.Add((uri, options.ReplaceHistoryEntry));
    }
}
