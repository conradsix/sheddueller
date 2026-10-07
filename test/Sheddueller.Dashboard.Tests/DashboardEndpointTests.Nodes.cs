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
    public async Task Nodes_KnownData_RendersRegistry()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/nodes");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Nodes");
        html.ShouldContain("Real-time liveness monitoring and local capacity tracking across the cluster.");
        html.ShouldContain("Total Nodes");
        html.ShouldContain("cluster-wide");
        html.ShouldContain("Active");
        html.ShouldContain("Stale");
        html.ShouldContain("Dead");
        html.ShouldContain("nodes-summary-card nodes-summary-card--dead");
        html.ShouldContain("Node Registry");
        html.ShouldContain("sd-table-shell");
        html.ShouldContain("sd-table-controls");
        html.ShouldContain("sd-table-search");
        html.ShouldContain("sd-table-select nodes-health-filter");
        html.ShouldContain("Filter node ID");
        html.ShouldContain("Filter by health state");
        AssertAppearsBefore(html, "aria-label=\"Filter by health state\"", "aria-label=\"Filter node ID\"");
        html.ShouldContain("wrk-prod-us-east-1a-8f92");
        html.ShouldContain("wrk-prod-eu-west-1a-7b22");
        html.ShouldContain("wrk-prod-ap-south-1b-1f00");
        html.ShouldContain("2026-04-20 12:00:00 UTC");
        html.ShouldContain("2026-04-20 12:04:30 UTC");
        html.ShouldContain("14,205");
        html.ShouldContain("3 / 4");
        html.ShouldContain("Load More Records");
        html.ShouldContain("Showing 1-3 of 3 nodes");
        AssertShellRefresh(html);
    }

    [Fact]
    public async Task Nodes_HealthSummary_RendersFromNodeCountsWithoutMetricsData()
    {
        await using var app = await CreateStartedDashboardAsync(registerMetricsReader: false);
        var html = await GetOkHtmlAsync(app, "/sheddueller/nodes");

        html.ShouldContain("Total Nodes");
        html.ShouldContain("<strong>3</strong>");
        html.ShouldContain("cluster-wide");
        html.ShouldContain("33.3%");
        html.ShouldContain("Showing 1-3 of 3 nodes");
    }
}
