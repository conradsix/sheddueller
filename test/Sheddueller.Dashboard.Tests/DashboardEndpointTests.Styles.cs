namespace Sheddueller.Dashboard.Tests;

using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.TestHost;

using Shouldly;

public sealed partial class DashboardEndpointTests
{
    [Theory]
    [InlineData(true, "jobs", "pages/jobs.css")]
    [InlineData(false, "jobs", "pages/jobs.css")]
    [InlineData(true, "", "pages/overview.css")]
    [InlineData(true, "schedules", "pages/schedules.css")]
    [InlineData(true, "nodes", "pages/nodes.css")]
    [InlineData(true, "metrics", "pages/metrics.css")]
    [InlineData(true, "concurrency-groups", "pages/concurrency-groups.css")]
    [InlineData(true, "settings", "pages/settings.css")]
    public async Task Styles_MountedDashboard_ServesPageAndLayoutStyles(bool minimalHosting, string route, string stylesheet)
    {
        await using var app = await CreateStartedDashboardAsync(mapWithWebApplication: minimalHosting, registerCleanupSettingsStore: true);
        var html = await GetOkHtmlAsync(app, "/sheddueller/" + route);
        html.ShouldContain("_content/Sheddueller.Dashboard/styles/" + stylesheet + "?v=");
        var urls = Regex.Matches(html, "_content/Sheddueller.Dashboard/styles/[^\"]+")
          .Select(match => match.Value).Distinct().ToArray();
        urls.Length.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var url in urls)
        {
            var response = await app.GetTestClient().GetAsync(new Uri("/sheddueller/" + WebUtility.HtmlDecode(url), UriKind.Relative));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, url);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("text/css");
            var css = await response.Content.ReadAsStringAsync();
            css.ShouldNotContain("@@media");
            if (url.Contains("concurrency-groups.css", StringComparison.Ordinal))
            {
                css.ShouldContain("groups-state--nominal");
            }
            if (url.Contains("dashboard-layout.css", StringComparison.Ordinal))
            {
                css.ShouldContain("#0b1326");
                css.ShouldContain("@media");
                css.ShouldContain("prefers-color-scheme: dark");
            }
        }
    }

    [Fact]
    public async Task Styles_JobDetail_ServesInvocationHighlightingRules()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, $"/sheddueller/jobs/{StubJobInspectionReader.JobId:D}");
        var url = Regex.Match(html, "_content/Sheddueller.Dashboard/styles/pages/job-detail.css[^\"]+").Value;
        url.ShouldNotBeEmpty();
        var response = await app.GetTestClient().GetAsync(new Uri("/sheddueller/" + WebUtility.HtmlDecode(url), UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var css = await response.Content.ReadAsStringAsync();
        css.ShouldContain("pre.job-detail-invocation-call[class*=\"language-\"]");
        css.ShouldContain("box-shadow: none;");
    }
}
