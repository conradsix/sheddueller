namespace Sheddueller.Dashboard.Tests;

using System.Globalization;
using System.Linq.Expressions;
using System.Net;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Sheddueller;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.ConcurrencyGroups;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Metrics;
using Sheddueller.Inspection.Nodes;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Serialization;
using Sheddueller.Storage;

using Shouldly;

using JobsPage = Sheddueller.Dashboard.Components.Pages.Jobs;
using SchedulesPage = Sheddueller.Dashboard.Components.Pages.Schedules;

public sealed partial class DashboardEndpointTests
{
    [Fact]
    public async Task Schedules_KnownData_RendersRegistry()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/schedules");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Schedules");
        html.ShouldContain("Manage recurring operational tasks and cron triggers.");
        html.ShouldContain("Schedule Registry");
        html.ShouldContain("sd-table-shell");
        html.ShouldContain("sd-table-controls");
        html.ShouldContain("sd-table-search");
        html.ShouldContain("sd-table-select schedules-state-filter");
        html.ShouldContain("Filter by exact schedule key");
        html.ShouldContain("Filter by pause state");
        AssertAppearsBefore(html, "aria-label=\"Filter by pause state\"", "aria-label=\"Filter by exact schedule key\"");
        html.ShouldContain("etl_nightly_sync");
        html.ShouldContain("cache_eviction_hourly");
        html.ShouldContain("billing_reconciliation");
        html.ShouldContain("Sheddueller.Dashboard.Tests.Schedules.EtlSyncWorker.Run");
        html.ShouldContain("0 2 * * *");
        html.ShouldContain("Active");
        html.ShouldContain("Paused");
        html.ShouldContain("Skip");
        html.ShouldContain("Allow");
        html.ShouldContain("etl-nodes");
        html.ShouldContain("finance-secure");
        html.ShouldContain("tenant:acme");
        html.ShouldContain("schedule:nightly");
        html.ShouldContain("2026-04-20 12:03:00 UTC");
        html.ShouldContain("Trigger Now etl_nightly_sync");
        html.ShouldContain("Trigger Now cache_eviction_hourly");
        html.ShouldContain("Pause Schedule etl_nightly_sync");
        html.ShouldContain("Resume Schedule cache_eviction_hourly");
        html.ShouldContain("Load More Records");
        html.ShouldContain("Showing 1-3 of 3 schedules with more available");
        AssertShellRefresh(html);
    }

    [Fact]
    public void Schedules_TriggerActionMessages_FormatSuccessSkippedAndMissingCases()
    {
        SchedulesPage.CreateTriggerSuccessMessage("etl_nightly_sync")
          .ShouldBe("Schedule etl_nightly_sync triggered as job");
        SchedulesPage.CreateTriggerSkippedMessage("etl_nightly_sync")
          .ShouldBe("Schedule etl_nightly_sync already has an active occurrence.");
        SchedulesPage.ScheduleNotFoundActionFailureMessage
          .ShouldBe("Schedule action failed: Schedule was not found.");
    }
}
