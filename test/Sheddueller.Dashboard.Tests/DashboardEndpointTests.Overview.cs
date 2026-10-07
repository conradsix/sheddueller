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
    public async Task Overview_KnownData_RendersOperationalSummary()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Dashboard");
        html.ShouldContain("Overview");
        html.ShouldContain("_framework/blazor.web.js");
        html.ShouldContain("prefers-color-scheme: dark");
        html.ShouldContain("_content/Sheddueller.Dashboard/styles/dashboard-layout.css?v=");
        AssertShellRefresh(html);
        html.ShouldContain("Running Jobs");
        html.ShouldContain("Recently Failed");
        html.ShouldContain("Queued (Next Up)");
        html.ShouldContain("Started");
        html.ShouldContain("Terminated");
        html.ShouldContain("Tags");
        html.ShouldContain("Error");
        html.ShouldNotContain("<th>State</th>");
        html.ShouldContain("tenant:acme");
        html.ShouldContain("schedule:daily_rollup");
        html.ShouldContain("2026-04-20 12:05:00 UTC");
        html.ShouldContain("2026-04-20 12:04:00 UTC");
        html.ShouldContain("href=\"jobs?state=Claimed\"");
        html.ShouldContain("href=\"jobs?state=Failed\"");
        html.ShouldContain("href=\"jobs?handler=StubService.Run\"");
        html.ShouldContain("href=\"jobs?tag=tenant%3Aacme\"");
        html.ShouldContain($"href=\"jobs/{StubJobInspectionReader.JobId:D}\"");
    }
}
