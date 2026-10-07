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
    public async Task Jobs_KnownData_RendersSearchResults()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/jobs");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Jobs");
        html.ShouldContain("Job filters");
        html.ShouldContain("Filter by status");
        html.ShouldContain("Filter by handler substring");
        html.ShouldContain("Filter by tag substring");
        html.ShouldContain("Filter by concurrency group substring");
        html.ShouldContain("Sort jobs");
        html.ShouldContain("Operational Order");
        html.ShouldContain("Newest First");
        html.ShouldContain("Clear Filters");
        html.ShouldContain("Clear Queued");
        html.ShouldContain("Cancel all queued, delayed, and retry-waiting jobs. Type delete to confirm.");
        html.ShouldNotContain("Expand query filters");
        html.ShouldNotContain("Execute Query");
        html.ShouldNotContain("Query Parameters");
        AssertAppearsBefore(html, "aria-label=\"Filter by status\"", "aria-label=\"Filter by handler substring\"");
        html.ShouldContain("Tags");
        html.ShouldContain("tenant:acme");
        html.ShouldContain("schedule:daily_rollup");
        html.ShouldContain("Groups");
        html.ShouldContain("tenant-acme");
        html.ShouldContain("daily-rollup");
        html.ShouldContain("href=\"jobs?state=Claimed\"");
        html.ShouldContain("href=\"jobs?handler=StubService.Run\"");
        html.ShouldContain("href=\"jobs?tag=tenant%3Aacme\"");
        html.ShouldContain("href=\"jobs?group=tenant-acme\"");
        AssertShellRefresh(html);
        html.ShouldContain($"href=\"jobs/{StubJobInspectionReader.JobId:D}\"");
        html.ShouldContain(">Queue</span>");
        html.ShouldContain("Running");
        html.ShouldContain("#1");
    }

    [Fact]
    public async Task Jobs_ClaimedAndQueuedFilters_RendersClaimedBeforeQueued()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/jobs?state=Queued&state=Claimed");

        AssertStatusCheckbox(html, "Queued", isChecked: true);
        AssertStatusCheckbox(html, "Claimed", isChecked: true);
        AssertAppearsBefore(html, $"href=\"jobs/{StubJobInspectionReader.JobId:D}\"", $"href=\"jobs/{StubJobInspectionReader.QueuedJobId:D}\"");
        html.ShouldContain("Running");
        html.ShouldContain("#1");
    }

    [Fact]
    public async Task Jobs_SortQuery_RendersSelectedSortAndPreservesQuickLinks()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/jobs?state=Queued&sort=NewestFirst");

        AssertSelectValue(html, "Sort jobs", "NewestFirst");
        html.ShouldContain("href=\"jobs?state=Claimed&amp;sort=NewestFirst\"");
        html.ShouldContain("href=\"jobs?state=Queued&amp;handler=StubService.Run&amp;sort=NewestFirst\"");
    }

    [Fact]
    public async Task Jobs_TagDisplayOrder_ConfiguredNamesLeadCompactChips()
    {
        await using var app = await CreateStartedDashboardAsync(configureDashboard: options =>
        {
            options.TagDisplayOrder = ["domain", "tenant"];
        });
        var html = await GetOkHtmlAsync(app, "/sheddueller/jobs");

        AssertAppearsBefore(html, "href=\"jobs?tag=domain%3Apayments\"", "href=\"jobs?tag=tenant%3Aacme\"");
        html.ShouldContain("jobs-chip--overflow");
        html.ShouldContain("aria-haspopup=\"true\"");
        html.ShouldContain("&#x2B;2");
        html.ShouldContain("sd-chip-overflow__panel");
        html.ShouldContain("href=\"jobs?tag=schedule%3Adaily_rollup\"");
        html.ShouldContain("href=\"jobs?tag=source%3Astub\"");
        html.ShouldContain("aria-label=\"Additional tags: domain:payments, tenant:acme, schedule:daily_rollup, source:stub\"");
    }

    [Fact]
    public async Task Jobs_SharedDefaultView_RendersConfiguredFiltersAndPromotedTagColumn()
    {
        await using var app = await CreateStartedDashboardAsync(configureDashboard: options =>
        {
            options.JobViews =
            [
                new ShedduellerDashboardJobView("Failures")
                {
                    States = [JobState.Claimed],
                    Sort = JobInspectionSort.NewestFirst,
                    Columns =
                    [
                        new(ShedduellerDashboardJobColumnKind.JobId),
                        new(ShedduellerDashboardJobColumnKind.Handler),
                        new(ShedduellerDashboardJobColumnKind.Tag, "tenant", "Customer"),
                        new(ShedduellerDashboardJobColumnKind.Attempts),
                    ],
                },
            ];
            options.DefaultJobViewName = "Failures";
        });
        var html = await GetOkHtmlAsync(app, "/sheddueller/jobs");

        html.ShouldContain("Shared Presets");
        html.ShouldContain("Failures");
        AssertStatusCheckbox(html, "Claimed", isChecked: true);
        AssertSelectValue(html, "Sort jobs", "NewestFirst");
        html.ShouldContain(">Customer</span>");
        html.ShouldContain("jobs-derived-tag");
        html.ShouldContain(">acme</a>");
        html.ShouldContain("href=\"jobs?state=Claimed&amp;tag=tenant%3Aacme&amp;sort=NewestFirst\"");
        html.ShouldNotContain(">Progress</span>");
        html.ShouldNotContain(">Tags</span>");

        var filteredHtml = await GetOkHtmlAsync(app, "/sheddueller/jobs?state=Failed");
        AssertStatusCheckbox(filteredHtml, "Failed", isChecked: true);
        AssertStatusCheckbox(filteredHtml, "Claimed", isChecked: false);
        filteredHtml.ShouldContain(">Customer</span>");
    }

    [Fact]
    public async Task Jobs_BuiltInView_HidesProgressAndRendersViewControls()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/jobs");

        html.ShouldContain("aria-label=\"Select job view\"");
        html.ShouldContain("Save As");
        html.ShouldContain("Set Default");
        html.ShouldContain(">Columns</span>");
        html.ShouldNotContain("Default &#xB7; Default");
        html.ShouldNotContain(">Save</span>");
        html.ShouldNotContain(">Progress</span>");
        html.ShouldContain(">Disposition</span>");
        html.ShouldContain("style=\"width: 1568px; min-width: 1568px\"");
        html.ShouldContain("data-sd-column-resizer");
        html.ShouldContain("jobs-timestamp jobs-timestamp--inline");
        html.ShouldContain("jobs-timestamp__separator");
        html.ShouldContain(">12:00 UTC</span>");
        html.ShouldContain("title=\"2026-04-20 12:00:00 UTC\"");
    }

    [Fact]
    public async Task Jobs_QueryFilters_RendersControlsAndPreservingQuickLinks()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/jobs?state=Failed&handler=StubService.Run&tag=tenant%3Aacme&group=tenant-acme");

        AssertStatusCheckbox(html, "Failed", isChecked: true);
        AssertStatusCheckbox(html, "Queued", isChecked: false);
        html.ShouldContain("value=\"StubService.Run\"");
        html.ShouldContain("value=\"tenant:acme\"");
        html.ShouldContain("value=\"tenant-acme\"");
        html.ShouldContain("href=\"jobs?state=Claimed&amp;handler=StubService.Run&amp;tag=tenant%3Aacme&amp;group=tenant-acme\"");
        html.ShouldContain("href=\"jobs?state=Failed&amp;handler=StubService.Run&amp;tag=tenant%3Aacme&amp;group=tenant-acme\"");
        html.ShouldContain("href=\"jobs?state=Failed&amp;handler=StubService.Run&amp;tag=schedule%3Adaily_rollup&amp;group=tenant-acme\"");
        html.ShouldContain("href=\"jobs?state=Failed&amp;handler=StubService.Run&amp;tag=tenant%3Aacme&amp;group=daily-rollup\"");
    }

    [Fact]
    public void Jobs_ClearQueuedActionMessage_FormatsCanceledCount()
    {
        JobsPage.CreateClearQueuedSuccessMessage(1234)
          .ShouldBe("Canceled 1,234 queued job(s).");
    }
}
