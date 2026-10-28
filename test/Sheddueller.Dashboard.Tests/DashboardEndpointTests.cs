namespace Sheddueller.Dashboard.Tests;

using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardEndpointTests
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
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("_framework/blazor.web.js");
        html.ShouldNotContain("sd-sidebar");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapShedduellerDashboard_InteractiveCircuit_NegotiatesBlazorHub(bool mapWithWebApplication)
    {
        await using var app = await CreateStartedDashboardAsync(mapWithWebApplication);
        using var content = new StringContent(string.Empty);

        var response = await app.GetTestClient().PostAsync(
          new Uri("/sheddueller/_blazor/negotiate?negotiateVersion=1", UriKind.Relative),
          content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("connectionToken");
    }

    [Fact]
    public async Task DashboardDocument_DefaultOptions_LoadsNoThirdPartyResources()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, "/sheddueller/");

        var externalReferences = Regex.Matches(html, "(?:src|href)=\"(?<url>(?:https?:)?//[^\"]+)\"")
          .Select(match => match.Groups["url"].Value)
          .Where(url => !url.StartsWith("http://localhost/", StringComparison.Ordinal))
          .ToArray();

        externalReferences.ShouldBeEmpty();
    }

    // No test renders a page: they stop at a redirect, the hub negotiation or the unrendered document. The job reader is
    // registered so that if the overview were ever prerendered, the prerender test fails on its assertion rather than on
    // a missing service.
    private static async Task<WebApplication> CreateStartedDashboardAsync(bool mapWithWebApplication = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IJobInspectionReader, EmptyJobInspectionReader>();
        builder.Services.AddShedduellerDashboard();

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

    private sealed class EmptyJobInspectionReader : IJobInspectionReader
    {
        public ValueTask<JobInspectionOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new JobInspectionOverview(new Dictionary<JobState, int>(), [], [], [], [], []));

        public ValueTask<JobInspectionPage> SearchJobsAsync(
            JobInspectionQuery query,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new JobInspectionPage([], ContinuationToken: null, TotalCount: 0));

        public ValueTask<JobInspectionDetail?> GetJobAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult<JobInspectionDetail?>(null);

        public ValueTask<JobQueuePosition> GetQueuePositionAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new JobQueuePosition(jobId, JobQueuePositionKind.NotFound, Position: null));

        public IAsyncEnumerable<JobEvent> ReadEventsAsync(
            Guid jobId,
            JobEventReadOptions? options = null,
            CancellationToken cancellationToken = default)
          => AsyncEnumerable.Empty<JobEvent>();
    }
}
