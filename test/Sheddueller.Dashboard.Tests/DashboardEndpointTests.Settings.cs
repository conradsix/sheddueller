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
    public async Task Settings_KnownData_RendersCleanupControls()
    {
        await using var app = await CreateStartedDashboardAsync(registerCleanupSettingsStore: true);
        var html = await GetOkHtmlAsync(app, "/sheddueller/settings");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Settings");
        html.ShouldContain("Persisted cluster cleanup controls.");
        html.ShouldContain("Terminal Jobs");
        html.ShouldContain("Cleanup enabled");
        html.ShouldContain("Completed retention");
        html.ShouldContain("Failed retention");
        html.ShouldContain("Canceled retention");
        html.ShouldContain("Job Events");
        html.ShouldContain("Event retention");
        html.ShouldContain("Metrics");
        html.ShouldContain("Metrics retention");
        html.ShouldContain("Save Settings");
        html.ShouldContain("Reload");
        html.ShouldContain("value=\"48\"");
        html.ShouldContain("value=\"72\"");
        html.ShouldContain("value=\"250\"");
    }

    [Fact]
    public async Task Settings_UnsupportedProvider_RendersUnavailableState()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/settings");

        html.ShouldContain("Settings");
        html.ShouldContain("Persisted settings unavailable");
        html.ShouldContain("does not expose dashboard-editable cleanup settings");
    }
}
