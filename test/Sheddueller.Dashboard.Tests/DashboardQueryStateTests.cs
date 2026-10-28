namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Nodes;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardQueryStateTests
{
    private const string BaseUri = "http://localhost/sheddueller/";

    [Fact]
    public void JobListLocation_FiltersViewAndDepth_RoundTrip()
    {
        var filters = new DashboardJobFilters();
        filters.SetStateSelected(JobState.Failed, selected: true);
        filters.SetHandlerContains("BillingWorker.Run");
        filters.SetSort(JobInspectionSort.NewestFirst);

        var href = DashboardJobListLocation.ToHref(
          filters,
          viewFilters: new DashboardJobFilters(),
          viewParameter: "shared:Failures",
          take: 75);
        var location = DashboardJobListLocation.Parse(BaseUri + href);

        href.ShouldBe("jobs?state=Failed&handler=BillingWorker.Run&sort=NewestFirst&view=shared%3AFailures&take=75");
        location.ViewKey.ShouldBe("shared:Failures");
        location.Take.ShouldBe(75);
        location.Filters.ShouldNotBeNull();
        location.Filters.Matches(filters).ShouldBeTrue();
    }

    [Fact]
    public void JobListLocation_DefaultViewAndFirstPage_OmitsViewAndDepth()
    {
        var filters = new DashboardJobFilters();
        filters.SetStateSelected(JobState.Queued, selected: true);

        var href = DashboardJobListLocation.ToHref(
          filters,
          viewFilters: new DashboardJobFilters(),
          viewParameter: null,
          take: DashboardJobListLocation.DefaultTake);

        href.ShouldBe("jobs?state=Queued");
    }

    [Theory]
    [InlineData("built-in", "built-in", "built-in", null)]
    [InlineData("shared:Failures", "built-in", "built-in", "shared:Failures")]
    [InlineData("built-in", "personal:abc", "built-in", "built-in")]
    [InlineData("personal:abc", "personal:abc", "built-in", "personal:abc")]
    [InlineData("shared:Failures", "shared:Failures", "shared:Failures", null)]
    public void JobListLocation_ViewParameter_OmittedOnlyForPreferredConfiguredDefault(
        string selectedViewKey,
        string preferredViewKey,
        string configuredDefaultViewKey,
        string? expected)
      => DashboardJobListLocation.ViewParameterValue(selectedViewKey, preferredViewKey, configuredDefaultViewKey)
        .ShouldBe(expected);

    [Fact]
    public void JobListLocation_NoFilterQuery_UsesViewFilters()
    {
        var location = DashboardJobListLocation.Parse(BaseUri + "jobs?view=shared%3AFailures");

        location.Filters.ShouldBeNull();
        location.ViewKey.ShouldBe("shared:Failures");
        location.Take.ShouldBe(DashboardJobListLocation.DefaultTake);
    }

    [Fact]
    public void JobListLocation_ClearedFiltersOnFilteredView_StaysExplicitlyEmpty()
    {
        var viewFilters = new DashboardJobFilters();
        viewFilters.SetStateSelected(JobState.Failed, selected: true);

        var href = DashboardJobListLocation.ToHref(
          new DashboardJobFilters(),
          viewFilters,
          viewParameter: null,
          take: DashboardJobListLocation.DefaultTake);
        var location = DashboardJobListLocation.Parse(BaseUri + href);

        href.ShouldBe("jobs?sort=Operational");
        location.Filters.ShouldNotBeNull();
        location.Filters.HasAppliedFilters.ShouldBeFalse();
    }

    [Fact]
    public void JobListLocation_FiltersMatchingView_DoNotForceSort()
    {
        var href = DashboardJobListLocation.ToHref(
          new DashboardJobFilters(),
          new DashboardJobFilters(),
          viewParameter: null,
          take: DashboardJobListLocation.DefaultTake);

        href.ShouldBe("jobs");
    }

    [Theory]
    [InlineData("jobs?take=abc", 25)]
    [InlineData("jobs?take=-10", 25)]
    [InlineData("jobs?take=3", 25)]
    [InlineData("jobs?take=50", 50)]
    [InlineData("jobs?take=999999", 1000)]
    public void JobListLocation_TakeParameter_IsClampedToSupportedDepth(
        string relativeUri,
        int expectedTake)
      => DashboardJobListLocation.Parse(BaseUri + relativeUri).Take.ShouldBe(expectedTake);

    [Fact]
    public void JobFilterQuery_LinkWithView_PreservesSelectedView()
    {
        var filters = new DashboardJobFilters();
        filters.SetSort(JobInspectionSort.NewestFirst);

        DashboardJobFilterQuery.WithStateHref(filters, JobState.Claimed, "personal:abc")
          .ShouldBe("jobs?state=Claimed&sort=NewestFirst&view=personal%3Aabc");
        DashboardJobFilterQuery.WithGroupHref(filters, "tenant-acme")
          .ShouldBe("jobs?group=tenant-acme&sort=NewestFirst");
    }

    [Fact]
    public void JobFilters_Matches_IgnoresSurroundingWhitespace()
    {
        var typed = new DashboardJobFilters();
        typed.SetHandlerContains(" BillingWorker ");
        var saved = new DashboardJobFilters();
        saved.SetHandlerContains("BillingWorker");

        typed.Matches(saved).ShouldBeTrue();

        saved.SetSort(JobInspectionSort.NewestFirst);
        typed.Matches(saved).ShouldBeFalse();
    }

    [Fact]
    public void ScheduleFilterQuery_Filters_RoundTrip()
    {
        var filters = new DashboardScheduleFilters
        {
            ScheduleKey = "nightly rollup",
            PauseState = DashboardScheduleFilters.PausedStateFilter,
        };

        var href = DashboardScheduleFilterQuery.ToHref(filters);
        var parsed = DashboardScheduleFilterQuery.ParseUri(BaseUri + href);

        href.ShouldBe("schedules?state=paused&key=nightly%20rollup");
        parsed.ScheduleKey.ShouldBe("nightly rollup");
        parsed.PauseState.ShouldBe(DashboardScheduleFilters.PausedStateFilter);
        DashboardScheduleFilterQuery.ToHref(new DashboardScheduleFilters()).ShouldBe("schedules");
    }

    [Fact]
    public void ScheduleFilterQuery_UnknownState_IsIgnored()
      => DashboardScheduleFilterQuery.ParseUri(BaseUri + "schedules?state=sleeping").PauseState.ShouldBeEmpty();

    [Fact]
    public void ScheduleFilters_CopyFrom_ReportsWhetherAnythingChanged()
    {
        var filters = new DashboardScheduleFilters();

        filters.CopyFrom(new DashboardScheduleFilters { ScheduleKey = "clean" }).ShouldBeTrue();
        filters.CopyFrom(new DashboardScheduleFilters { ScheduleKey = "clean" }).ShouldBeFalse();
        filters.ScheduleKey.ShouldBe("clean");
    }

    [Fact]
    public void ConcurrencyGroupFilterQuery_SwitchesAndSearch_RoundTrip()
    {
        var filters = new DashboardConcurrencyGroupFilters
        {
            GroupKey = "api",
            SaturatedOnly = true,
            RateLimitedOnly = false,
            HasBlockedJobsOnly = true,
        };

        var href = DashboardConcurrencyGroupFilterQuery.ToHref(filters);
        var parsed = DashboardConcurrencyGroupFilterQuery.ParseUri(BaseUri + href);

        href.ShouldBe("concurrency-groups?saturated=true&blocked=true&key=api");
        parsed.GroupKey.ShouldBe("api");
        parsed.SaturatedOnly.ShouldBeTrue();
        parsed.RateLimitedOnly.ShouldBeFalse();
        parsed.HasBlockedJobsOnly.ShouldBeTrue();
    }

    [Fact]
    public void NodeFilterQuery_StateAndSearch_RoundTripWithCanonicalState()
    {
        var parsed = DashboardNodeFilterQuery.ParseUri(BaseUri + "nodes?state=stale&node=eu-west");

        parsed.State.ShouldBe(nameof(NodeHealthState.Stale));
        parsed.NodeId.ShouldBe("eu-west");
        DashboardNodeFilterQuery.ToHref(parsed).ShouldBe("nodes?state=Stale&node=eu-west");
        DashboardNodeFilterQuery.ParseUri(BaseUri + "nodes?state=zombie").State.ShouldBeEmpty();
    }

    [Fact]
    public void NodeFilters_CopyFrom_KeepsNodeSearchWhenStateChanges()
    {
        var filters = new DashboardNodeFilters { NodeId = "eu-west" };

        filters.CopyFrom(new DashboardNodeFilters { State = nameof(NodeHealthState.Dead), NodeId = "eu-west" }).ShouldBeTrue();

        filters.State.ShouldBe(nameof(NodeHealthState.Dead));
        filters.NodeId.ShouldBe("eu-west");
    }

    [Theory]
    [InlineData("metrics?window=5m", 5)]
    [InlineData("metrics?window=24h", 1440)]
    [InlineData("metrics?window=1h", 60)]
    [InlineData("metrics", 60)]
    [InlineData("metrics?window=7m", 60)]
    [InlineData("metrics?window=bogus", 60)]
    public void MetricsWindowQuery_Window_ParsesOnlySupportedWindows(
        string relativeUri,
        int expectedMinutes)
    {
        TimeSpan[] windows = [TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromHours(24)];

        DashboardMetricsWindowQuery.ParseUri(BaseUri + relativeUri, windows, TimeSpan.FromHours(1))
          .ShouldBe(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void MetricsWindowQuery_DefaultWindow_IsOmitted()
    {
        DashboardMetricsWindowQuery.ToHref(TimeSpan.FromHours(1), TimeSpan.FromHours(1)).ShouldBe("metrics");
        DashboardMetricsWindowQuery.ToHref(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)).ShouldBe("metrics?window=5m");
        DashboardMetricsWindowQuery.ToHref(TimeSpan.FromHours(24), TimeSpan.FromHours(1)).ShouldBe("metrics?window=24h");
    }

    [Theory]
    [InlineData("jobs", true)]
    [InlineData("jobs/", true)]
    [InlineData("jobs?state=Failed", true)]
    [InlineData("JOBS#top", true)]
    [InlineData("jobs/8c32d457-9e7a-42bb-8947-0c8fa54743be", false)]
    [InlineData("schedules", false)]
    public void QueryString_IsPath_MatchesListPageOnly(
        string relativeUri,
        bool expected)
      => DashboardQueryString.IsPath(relativeUri, "jobs").ShouldBe(expected);

    [Fact]
    public void NavigationState_JobsListHref_DefaultsAndRejectsOtherPages()
    {
        var state = new DashboardNavigationState();

        state.JobsListHref.ShouldBe("jobs");

        state.JobsListHref = "jobs?state=Failed&take=50";
        state.JobsListHref.ShouldBe("jobs?state=Failed&take=50");

        state.JobsListHref = "schedules";
        state.JobsListHref.ShouldBe("jobs");
    }
}
