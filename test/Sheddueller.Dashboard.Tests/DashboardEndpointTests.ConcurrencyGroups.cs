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
    public async Task ConcurrencyGroups_KnownData_RendersRegistry()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/concurrency-groups");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Concurrency Groups");
        html.ShouldContain("Identify saturation and blocked work across resource pools.");
        html.ShouldContain("Group Registry");
        html.ShouldContain("sd-table-shell");
        html.ShouldContain("sd-table-controls");
        html.ShouldContain("sd-table-search");
        html.ShouldContain("sd-table-switch");
        html.ShouldContain("Filter by group key");
        html.ShouldContain("Saturated only");
        html.ShouldContain("Rate-limited only");
        html.ShouldContain("Has blocked jobs");
        html.ShouldContain("type=\"checkbox\"");
        AssertAppearsBefore(html, "Saturated only", "aria-label=\"Filter by group key\"");
        AssertAppearsBefore(html, "Has blocked jobs", "aria-label=\"Filter by group key\"");
        html.ShouldContain("pool_etl_heavy");
        html.ShouldContain("api_sync_workers");
        html.ShouldContain("bg_maintenance");
        html.ShouldContain("db_vacuum_ops");
        html.ShouldContain("Limit Source");
        html.ShouldContain("Effective Rate");
        html.ShouldContain("Rate Source");
        html.ShouldContain("Next Permit");
        html.ShouldContain("Override");
        html.ShouldContain("Code default");
        html.ShouldContain("Built-in default");
        html.ShouldNotContain("Edit Limit");
        html.ShouldNotContain("Reset Override");
        html.ShouldContain("Saturated");
        html.ShouldContain("High Load");
        html.ShouldContain("Blocked Work");
        html.ShouldContain("Rate Limited");
        html.ShouldContain("2026-04-20 12:02:01 UTC");
        html.ShouldContain("Load More Records");
        html.ShouldContain("Showing 1-4 of 4 groups with more available");
        AssertShellRefresh(html);
    }

    [Fact]
    public async Task ConcurrencyGroups_WithManager_RendersLimitEditActions()
    {
        await using var app = await CreateStartedDashboardAsync(registerConcurrencyGroupManager: true);
        var html = await GetOkHtmlAsync(app, "/sheddueller/concurrency-groups");

        html.ShouldContain("Actions");
        html.ShouldContain("Edit Limit");
        html.ShouldContain("Reset Override");
        html.ShouldContain("Edit Rate Limit");
        html.ShouldContain("Set Unlimited Rate");
        html.ShouldContain("Reset Rate Override");
        html.ShouldContain("aria-label=\"Edit limit for concurrency group pool_etl_heavy\"");
        html.ShouldContain("aria-label=\"Reset limit override for concurrency group pool_etl_heavy\"");
        html.ShouldContain("aria-label=\"Edit rate limit for concurrency group pool_etl_heavy\"");
        html.ShouldContain("aria-label=\"Set unlimited rate override for concurrency group pool_etl_heavy\"");
    }
}
