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
    public async Task MapShedduellerDashboard_MinimalHosting_RedirectsToCanonicalRoot()
    {
        await using var app = await CreateStartedDashboardAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/sheddueller", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location?.OriginalString.ShouldBe("/sheddueller/");
    }

    [Fact]
    public async Task MapShedduellerDashboard_ApplicationBranch_RedirectsToCanonicalRoot()
    {
        await using var app = await CreateStartedDashboardAsync(mapWithWebApplication: false);
        var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/sheddueller", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location?.OriginalString.ShouldBe("/sheddueller/");
    }

    [Fact]
    public async Task MapShedduellerDashboard_DefaultOptions_DoesNotPrerenderRouteContent()
    {
        await using var app = await CreateStartedDashboardAsync(prerender: false);
        var html = await GetOkHtmlAsync(app, "/sheddueller/");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("_framework/blazor.web.js");
        html.ShouldContain("_content/Sheddueller.Dashboard/vendor/prism/prism-dark.css\" rel=\"stylesheet\" media=\"(prefers-color-scheme: dark)\"");
        html.ShouldContain("_content/Sheddueller.Dashboard/vendor/sheddueller-throughput-chart.js?v=");
        html.ShouldNotContain("sd-sidebar__brand");
    }

    private static async Task<WebApplication> CreateStartedDashboardAsync(
        bool prerender = true,
        bool mapWithWebApplication = true,
        Action<ShedduellerDashboardOptions>? configureDashboard = null,
        bool registerMetricsReader = true,
        bool registerCleanupSettingsStore = false,
        bool registerConcurrencyGroupManager = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DashboardEndpointTests).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IJobInspectionReader, StubJobInspectionReader>();
        builder.Services.AddSingleton<IJobManager, StubJobManager>();
        builder.Services.AddSingleton<StubScheduleInspectionReader>();
        builder.Services.AddSingleton<IScheduleInspectionReader>(serviceProvider => serviceProvider.GetRequiredService<StubScheduleInspectionReader>());
        builder.Services.AddSingleton<IRecurringScheduleManager>(serviceProvider => serviceProvider.GetRequiredService<StubScheduleInspectionReader>());
        builder.Services.AddSingleton<IConcurrencyGroupInspectionReader, StubConcurrencyGroupInspectionReader>();
        if (registerConcurrencyGroupManager)
        {
            builder.Services.AddSingleton<IConcurrencyGroupManager, StubConcurrencyGroupManager>();
        }

        builder.Services.AddSingleton<INodeInspectionReader, StubNodeInspectionReader>();
        if (registerMetricsReader)
        {
            builder.Services.AddSingleton<IMetricsInspectionReader, StubMetricsInspectionReader>();
        }

        if (registerCleanupSettingsStore)
        {
            builder.Services.AddSingleton<IShedduellerCleanupConfigurationStore, StubCleanupConfigurationStore>();
        }

        builder.Services.AddSingleton<IDashboardThroughputReader, StubDashboardThroughputReader>();
        builder.Services.AddShedduellerDashboard(options =>
        {
            options.Prerender = prerender;
            configureDashboard?.Invoke(options);
        });

        var app = builder.Build();
        if (mapWithWebApplication)
        {
            app.MapShedduellerDashboard("/sheddueller");
        }
        else
        {
            ((IApplicationBuilder)app).MapShedduellerDashboard("/sheddueller");
        }

        await app.StartAsync();

        return app;
    }

    private static async Task<string> GetOkHtmlAsync(
        WebApplication app,
        string path)
    {
        var response = await app.GetTestClient().GetAsync(new Uri(path, UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertShellRefresh(string html)
    {
        html.ShouldContain("Live Refresh");
        html.ShouldContain("sd-live-status");
        html.ShouldContain("Updated ");
        html.ShouldContain("Auto-refresh On");
    }

    private static void AssertAppearsBefore(
        string html,
        string earlier,
        string later)
    {
        var earlierIndex = html.IndexOf(earlier, StringComparison.Ordinal);
        var laterIndex = html.IndexOf(later, StringComparison.Ordinal);

        earlierIndex.ShouldBeGreaterThanOrEqualTo(0);
        laterIndex.ShouldBeGreaterThanOrEqualTo(0);
        earlierIndex.ShouldBeLessThan(laterIndex);
    }

    private static void AssertStatusCheckbox(
        string html,
        string state,
        bool isChecked)
    {
        var markerIndex = html.IndexOf(string.Create(CultureInfo.InvariantCulture, $"<span>{state}</span>"), StringComparison.Ordinal);
        markerIndex.ShouldBeGreaterThanOrEqualTo(0);

        var startIndex = html.LastIndexOf("<label", markerIndex, StringComparison.Ordinal);
        var endIndex = html.IndexOf("</label>", markerIndex, StringComparison.Ordinal);

        startIndex.ShouldBeGreaterThanOrEqualTo(0);
        endIndex.ShouldBeGreaterThan(startIndex);

        var label = html[startIndex..(endIndex + "</label>".Length)];
        if (isChecked)
        {
            label.ShouldContain("checked");
            return;
        }

        label.ShouldNotContain("checked");
    }

    private static void AssertSelectValue(
        string html,
        string ariaLabel,
        string value)
    {
        var markerIndex = html.IndexOf(string.Create(CultureInfo.InvariantCulture, $"aria-label=\"{ariaLabel}\""), StringComparison.Ordinal);
        markerIndex.ShouldBeGreaterThanOrEqualTo(0);

        var startIndex = html.LastIndexOf("<select", markerIndex, StringComparison.Ordinal);
        var endIndex = html.IndexOf("</select>", markerIndex, StringComparison.Ordinal);

        startIndex.ShouldBeGreaterThanOrEqualTo(0);
        endIndex.ShouldBeGreaterThan(startIndex);

        var select = html[startIndex..(endIndex + "</select>".Length)];
        var selectOpen = select[..select.IndexOf('>', StringComparison.Ordinal)];
        if (selectOpen.Contains(string.Create(CultureInfo.InvariantCulture, $"value=\"{value}\""), StringComparison.Ordinal))
        {
            return;
        }

        var optionMarker = string.Create(CultureInfo.InvariantCulture, $"value=\"{value}\"");
        var optionMarkerIndex = select.IndexOf(optionMarker, StringComparison.Ordinal);
        optionMarkerIndex.ShouldBeGreaterThanOrEqualTo(0);
        var optionStartIndex = select.LastIndexOf("<option", optionMarkerIndex, StringComparison.Ordinal);
        var optionEndIndex = select.IndexOf("</option>", optionMarkerIndex, StringComparison.Ordinal);
        optionStartIndex.ShouldBeGreaterThanOrEqualTo(0);
        optionEndIndex.ShouldBeGreaterThan(optionStartIndex);
        var option = select[optionStartIndex..(optionEndIndex + "</option>".Length)];
        option.ShouldContain("selected");
    }

    private static void AssertCancelButton(
        string html,
        string expectedText,
        string expectedTitle,
        bool disabled)
    {
        var button = GetCancelButtonHtml(html);
        button.ShouldContain(expectedText);
        button.ShouldContain(expectedTitle);

        if (disabled)
        {
            button.ShouldContain("disabled");
            return;
        }

        button.ShouldNotContain("disabled");
    }

    private static string GetCancelButtonHtml(string html)
    {
        var markerIndex = html.IndexOf("job-detail-cancel-action", StringComparison.Ordinal);
        markerIndex.ShouldBeGreaterThanOrEqualTo(0);

        var startIndex = html.LastIndexOf("<button", markerIndex, StringComparison.Ordinal);
        var endIndex = html.IndexOf("</button>", markerIndex, StringComparison.Ordinal);

        startIndex.ShouldBeGreaterThanOrEqualTo(0);
        endIndex.ShouldBeGreaterThan(startIndex);

        return html[startIndex..(endIndex + "</button>".Length)];
    }
}
