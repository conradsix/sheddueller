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
    public async Task JobDetail_KnownJob_RendersDetailAndDefaultLogFilter()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, $"/sheddueller/jobs/{StubJobInspectionReader.JobId:D}");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Job Detail");
        html.ShouldContain(StubJobInspectionReader.JobId.ToString("D"));
        html.ShouldContain("CLAIMED");
        html.ShouldContain("Progress");
        html.ShouldContain("45% Processing records chunk 4 of 9");
        html.ShouldContain("Metadata");
        html.ShouldContain("tenant:acme");
        html.ShouldContain("tenant-acme");
        html.ShouldContain("href=\"jobs?state=Claimed\"");
        html.ShouldContain("href=\"jobs?handler=StubService.Run\"");
        html.ShouldContain("href=\"jobs?tag=tenant%3Aacme\"");
        html.ShouldContain("href=\"jobs?group=tenant-acme\"");
        html.ShouldContain("Invocation");
        html.ShouldContain("_content/Sheddueller.Dashboard/styles/pages/job-detail.css?v=");
        html.ShouldContain("StubService.Run(");
        html.ShouldContain("permanent-failure");
        html.ShouldContain("Job.Resolve");
        html.ShouldContain("StubDependency");
        html.ShouldContain("Job.Context");
        html.ShouldContain("scheduler-owned");
        html.ShouldContain("Lifecycle Timeline");
        html.ShouldContain("Attempt 1 Failed");
        html.ShouldContain("ConnectionTimeoutError: db-primary cluster unreachable");
        html.ShouldContain("Log Stream");
        html.ShouldContain("LIVE");
        html.ShouldContain("Updated ");
        html.ShouldContain("Show Progress");
        html.ShouldContain("type=\"checkbox\"");
        html.ShouldContain("Initializing worker environment");
        html.ShouldNotContain("progress event row should be hidden");
        AssertCancelButton(
          html,
          "Cancel Job",
          "Request cooperative cancellation for this running job.",
          disabled: false);
    }

    [Fact]
    public async Task JobDetail_QueuedJob_RendersEnabledCancelAction()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, $"/sheddueller/jobs/{StubJobInspectionReader.QueuedJobId:D}");

        AssertCancelButton(
          html,
          "Cancel Job",
          "Cancel this queued job before it is claimed.",
          disabled: false);
    }

    [Fact]
    public async Task JobDetail_CancellationRequestedJob_RendersDisabledCancelAction()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, $"/sheddueller/jobs/{StubJobInspectionReader.CancellationRequestedJobId:D}");

        AssertCancelButton(
          html,
          "Cancellation Requested",
          "Cancellation was already requested at 2026-04-20 12:08:30 UTC; the worker will observe it on heartbeat.",
          disabled: true);
    }

    [Fact]
    public async Task JobDetail_CompletedJob_RendersRunTime()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, $"/sheddueller/jobs/{StubJobInspectionReader.CompletedJobId:D}");

        html.ShouldContain("Run Time:");
        html.ShouldContain("4 m");
        html.ShouldContain("2026-04-20 12:09:00 UTC");
    }

    [Theory]
    [InlineData("a1543a1d-b7e0-4b43-b7ed-62249dc117be", "This job completed at 2026-04-20 12:09:00 UTC and cannot be canceled.")]
    [InlineData("b4f8131d-a097-4410-8f47-8d37387e1357", "This job failed at 2026-04-20 12:04:00 UTC and cannot be canceled.")]
    [InlineData("d271e83d-5657-4a4f-9644-dd3b07911efe", "This job was canceled at 2026-04-20 12:02:00 UTC and cannot be canceled again.")]
    public async Task JobDetail_TerminalJob_RendersDisabledCancelAction(
        string jobIdText,
        string expectedTitle)
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, $"/sheddueller/jobs/{jobIdText}");

        AssertCancelButton(
          html,
          "Cancel Job",
          expectedTitle,
          disabled: true);
    }

    [Fact]
    public async Task JobDetail_MissingJob_RendersNotFoundWithDisabledCancelAction()
    {
        await using var app = await CreateStartedDashboardAsync();
        var html = await GetOkHtmlAsync(app, $"/sheddueller/jobs/{StubJobInspectionReader.MissingJobId:D}");

        html.ShouldContain("base href=\"http://localhost/sheddueller/\"");
        html.ShouldContain("Job Detail");
        html.ShouldContain("Job Not Found");
        html.ShouldContain("The requested execution record could not be located.");
        html.ShouldContain(StubJobInspectionReader.MissingJobId.ToString("D"));
        html.ShouldContain("Return to Jobs");
        html.ShouldContain("href=\"jobs\"");
        AssertCancelButton(
          html,
          "Cancel Job",
          "No job was found with this ID.",
          disabled: true);
    }
}
