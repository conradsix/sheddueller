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
    public async Task Metrics_KnownData_RendersRollingHealth()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/metrics");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Metrics");
        html.ShouldContain("Live telemetry and comparative metrics for active scheduler nodes.");
        html.ShouldContain("5m");
        html.ShouldContain("1h");
        html.ShouldContain("24h");
        html.ShouldContain("metrics-window-tabs__button--selected");
        html.ShouldContain("Queue Depth");
        html.ShouldContain("Throughput Rate");
        html.ShouldContain("Schedule Fire Lag");
        html.ShouldContain("Live Throughput");
        html.ShouldContain("5s Buckets / 1h Window");
        html.ShouldContain("aria-label=\"Throughput series filters\"");
        html.ShouldContain("aria-pressed=\"true\"");
        html.ShouldContain("Failed Attempts");
        html.ShouldContain("Queue Latency");
        html.ShouldContain("Execution Duration");
        html.ShouldContain("Window Comparison");
        html.ShouldContain("Concurrency Saturation");
        html.ShouldContain("1,204");
        html.ShouldContain("842.3/min");
        html.ShouldContain("120 ms");
        html.ShouldContain("1.2 s");
        html.ShouldContain("2 saturated");
        AssertShellRefresh(html);
    }
}
