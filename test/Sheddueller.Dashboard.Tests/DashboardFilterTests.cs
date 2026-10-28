namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.ConcurrencyGroups;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Nodes;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardFilterTests
{
    [Fact]
    public void JobFilters_ToQuery_NormalizesLiveFilters()
    {
        var filters = new DashboardJobFilters();

        filters.SetStateSelected(JobState.Claimed, selected: true).ShouldBeTrue();
        filters.SetStateSelected(JobState.Queued, selected: true).ShouldBeTrue();
        filters.SetHandlerContains(" InvoiceHandler ").ShouldBeTrue();
        filters.SetTagContains(" tenant:acme ").ShouldBeTrue();
        filters.SetConcurrencyGroupContains(" acme ").ShouldBeTrue();
        filters.SetSort(JobInspectionSort.NewestFirst).ShouldBeTrue();

        var query = filters.ToQuery(pageSize: 25, continuationToken: "next");

        query.States.ShouldBe([JobState.Queued, JobState.Claimed]);
        query.HandlerContains.ShouldBe("InvoiceHandler");
        query.TagContains.ShouldBe("tenant:acme");
        query.ConcurrencyGroupContains.ShouldBe("acme");
        query.PageSize.ShouldBe(25);
        query.ContinuationToken.ShouldBe("next");
        query.Sort.ShouldBe(JobInspectionSort.NewestFirst);
        filters.HasAppliedFilters.ShouldBeTrue();
        filters.IsStateSelected(JobState.Claimed).ShouldBeTrue();
    }

    [Fact]
    public void JobFilters_Clear_ResetsFiltersAndIgnoresWhitespace()
    {
        var filters = new DashboardJobFilters();
        filters.SetStateSelected(JobState.Failed, selected: true);
        filters.SetHandlerContains("   ");
        filters.SetTagContains(" ");
        filters.SetConcurrencyGroupContains(" group ");

        filters.Clear();
        var query = filters.ToQuery(pageSize: 10, continuationToken: null);

        query.States.ShouldBeNull();
        query.HandlerContains.ShouldBeNull();
        query.TagContains.ShouldBeNull();
        query.ConcurrencyGroupContains.ShouldBeNull();
        query.Sort.ShouldBe(JobInspectionSort.Operational);
        filters.HasAppliedFilters.ShouldBeFalse();
        filters.IsStateSelected(JobState.Failed).ShouldBeFalse();
    }

    [Fact]
    public void JobFilterQuery_ParseQuery_NormalizesUrlFilters()
    {
        var filters = DashboardJobFilterQuery.ParseQuery(
          "?state=claimed&state=invalid&state=Queued&handler=Ignored.Run&handler=%20BillingWorker.Run%20&tag=tenant%3Aacme&tag=%20schedule%3Adaily%20&group=%20tenant-acme%20&sort=newestfirst");

        var query = filters.ToQuery(pageSize: 25, continuationToken: null);

        filters.SelectedStates.ShouldBe([JobState.Queued, JobState.Claimed]);
        filters.HandlerContains.ShouldBe("BillingWorker.Run");
        filters.TagContains.ShouldBe("schedule:daily");
        filters.ConcurrencyGroupContains.ShouldBe("tenant-acme");
        query.States.ShouldBe([JobState.Queued, JobState.Claimed]);
        query.HandlerContains.ShouldBe("BillingWorker.Run");
        query.TagContains.ShouldBe("schedule:daily");
        query.ConcurrencyGroupContains.ShouldBe("tenant-acme");
        query.Sort.ShouldBe(JobInspectionSort.NewestFirst);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("?utm_source=dashboard", false)]
    [InlineData("?state=Failed", true)]
    [InlineData("?tag=provider%3Astreamline", true)]
    [InlineData("?sort=NewestFirst", true)]
    public void JobFilterQuery_RecognizedQuery_DetectsOnlyJobViewFilters(
        string query,
        bool expected)
      => DashboardJobFilterQuery.HasRecognizedQuery(query).ShouldBe(expected);

    [Fact]
    public void JobFilterQuery_LinkGeneration_ReplacesClickedDimensionAndPreservesOthers()
    {
        var filters = new DashboardJobFilters();
        filters.SetStateSelected(JobState.Failed, selected: true);
        filters.SetStateSelected(JobState.Queued, selected: true);
        filters.SetHandlerContains("OldWorker.Run");
        filters.SetTagContains("tenant:acme");
        filters.SetConcurrencyGroupContains("tenant acme");
        filters.SetSort(JobInspectionSort.NewestFirst);

        DashboardJobFilterQuery.WithGroupHref(filters, "billing")
          .ShouldBe("jobs?state=Queued&state=Failed&handler=OldWorker.Run&tag=tenant%3Aacme&group=billing&sort=NewestFirst");
        DashboardJobFilterQuery.WithHandlerHref(filters, "BillingWorker.Run")
          .ShouldBe("jobs?state=Queued&state=Failed&handler=BillingWorker.Run&tag=tenant%3Aacme&group=tenant%20acme&sort=NewestFirst");
        DashboardJobFilterQuery.WithTagHref(filters, "schedule:daily")
          .ShouldBe("jobs?state=Queued&state=Failed&handler=OldWorker.Run&tag=schedule%3Adaily&group=tenant%20acme&sort=NewestFirst");
        DashboardJobFilterQuery.TagHref("tenant:acme west").ShouldBe("jobs?tag=tenant%3Aacme%20west");
    }

    [Fact]
    public void JobFilterQuery_SharedLinks_FilterTheJobsList()
    {
        DashboardJobFilterQuery.StateHref(JobState.Queued).ShouldBe("jobs?state=Queued");
        DashboardJobFilterQuery.RecentlyFailedHref().ShouldBe("jobs?state=Failed&sort=NewestFirst");
        DashboardJobFilterQuery.QueuedInGroupHref("tenant acme").ShouldBe("jobs?state=Queued&group=tenant%20acme");
        DashboardJobFilterQuery.HandlerHref(CreateSchedule("Acme.Jobs.RateLoader, Acme.Jobs", "LoadRates"))
          .ShouldBe("jobs?handler=RateLoader.LoadRates");
    }

    [Fact]
    public void ListLinks_OneItem_FilterItsListByKey()
    {
        DashboardScheduleFilterQuery.ScheduleHref("nightly rollup").ShouldBe("schedules?key=nightly%20rollup");
        DashboardConcurrencyGroupFilterQuery.GroupHref("tenant-acme").ShouldBe("concurrency-groups?key=tenant-acme");
        DashboardNodeFilterQuery.NodeHref("wrk-eu-1").ShouldBe("nodes?node=wrk-eu-1");
    }

    [Fact]
    public void ScheduleFilters_ToQuery_NormalizesLiveFilters()
    {
        var filters = new DashboardScheduleFilters
        {
            ScheduleKey = "  CLEAN  ",
            PauseState = DashboardScheduleFilters.PausedStateFilter,
        };

        var query = filters.ToQuery(pageSize: 25, continuationToken: "next");

        query.ScheduleKey.ShouldBe("CLEAN");
        query.IsPaused.ShouldBe(true);
        query.ServiceType.ShouldBeNull();
        query.MethodName.ShouldBeNull();
        query.Tag.ShouldBeNull();
        query.PageSize.ShouldBe(25);
        query.ContinuationToken.ShouldBe("next");
        filters.HasAppliedFilters.ShouldBeTrue();
    }

    [Fact]
    public void NodeFilters_ClientFilter_MatchesNodeIdCaseInsensitive()
    {
        var filters = new DashboardNodeFilters
        {
            NodeId = "EU-WEST",
            State = nameof(NodeHealthState.Active),
        };
        var nodes = new[]
        {
            CreateNode("wrk-prod-eu-west-1a"),
            CreateNode("wrk-prod-us-east-1a"),
        };

        var filtered = filters.ApplyClientFilter(nodes);
        var query = filters.ToQuery(pageSize: 50, continuationToken: "cursor");

        filtered.Select(node => node.NodeId).ShouldBe(["wrk-prod-eu-west-1a"]);
        query.State.ShouldBe(NodeHealthState.Active);
        query.PageSize.ShouldBe(50);
        query.ContinuationToken.ShouldBe("cursor");
    }

    [Fact]
    public void ConcurrencyGroupFilters_QueryAndClientFilter_MapSwitchesAndSubstring()
    {
        var filters = new DashboardConcurrencyGroupFilters
        {
            GroupKey = "API",
            SaturatedOnly = true,
            HasBlockedJobsOnly = true,
            RateLimitedOnly = true,
        };
        var groups = new[]
        {
            CreateGroup("api_sync_workers"),
            CreateGroup("etl_heavy"),
        };

        var filtered = filters.ApplyClientFilter(groups);
        var query = filters.ToQuery(pageSize: 10, continuationToken: null);

        filtered.Select(group => group.GroupKey).ShouldBe(["api_sync_workers"]);
        query.GroupKey.ShouldBeNull();
        query.IsSaturated.ShouldBe(true);
        query.HasBlockedJobs.ShouldBe(true);
        query.IsRateLimited.ShouldBe(true);
        query.PageSize.ShouldBe(10);
    }

    private static ScheduleInspectionSummary CreateSchedule(
        string serviceType,
        string methodName)
      => new(
        "nightly",
        serviceType,
        methodName,
        "0 2 * * *",
        IsPaused: false,
        RecurringOverlapMode.Skip,
        NextFireAtUtc: null,
        Priority: 0,
        ConcurrencyGroupKeys: [],
        Tags: [],
        DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture));

    private static NodeInspectionSummary CreateNode(string nodeId)
      => new(
        nodeId,
        NodeHealthState.Active,
        DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-04-20T12:05:00Z", CultureInfo.InvariantCulture),
        ClaimedJobCount: 1,
        MaxConcurrentExecutionsPerNode: 4,
        CurrentExecutionCount: 1);

    private static ConcurrencyGroupInspectionSummary CreateGroup(string groupKey)
      => new(
        groupKey,
        EffectiveLimit: 4,
        CurrentOccupancy: 1,
        BlockedJobCount: 0,
        IsSaturated: false,
        UpdatedAtUtc: DateTimeOffset.Parse("2026-04-20T12:05:00Z", CultureInfo.InvariantCulture));
}
