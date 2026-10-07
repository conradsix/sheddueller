namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dashboard operation failures are displayed without terminating the rendering circuit.")]
public partial class Overview
{
    private JobInspectionOverview? _overview;
    private string? _loadError;

    protected override async Task OnInitializedAsync()
    {
        this.InitializeLiveRefresh(
          this.RefreshOverviewAsync,
          showRefreshing: true,
          lastUpdatedUtc: nowUtc => this.LiveRefresh.LastUpdatedUtc ?? (this._overview is null ? null : nowUtc));
        this.LiveUpdates.JobEventPublished += this.OnJobEventPublishedAsync;
        await this.LoadOverviewAsync();
    }

    private protected override ValueTask DisposePageAsync()
    {
        this.LiveUpdates.JobEventPublished -= this.OnJobEventPublishedAsync;
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnJobEventPublishedAsync(
        JobEvent jobEvent,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        await this.ApplyPageStateAsync(() => this.LiveRefresh.MarkLiveActivity(jobEvent.OccurredAtUtc));
        this.LiveRefresh.QueueRefresh();
    }

    private async Task LoadOverviewAsync()
    {
        try
        {
            var overview = await this.Reader.GetOverviewAsync();
            this._overview = overview;
            this._loadError = null;
            this.LiveRefresh.MarkUpdated();
            this.NotifyShellRefreshChanged();
            await this.InvokeAsync(this.StateHasChanged);
        }
        catch (Exception exception)
        {
            this._loadError = exception.Message;
        }
    }

    private async Task RefreshOverviewAsync(CancellationToken cancellationToken)
    {
        var overview = await this.Reader.GetOverviewAsync(cancellationToken);

        await this.ApplyPageStateAsync(() =>
        {
            this._overview = overview;
            this._loadError = null;
        });
    }

    private static int GetStateCount(JobInspectionOverview overview, JobState state)
      => overview.StateCounts.TryGetValue(state, out var count) ? count : 0;

    private static string StateFilterHref(JobState state)
      => DashboardJobFilterQuery.StateHref(state);

    private static string StateFilterTitle(JobState state)
      => string.Concat("Filter jobs by state ", state);

    private static string HandlerFilterHref(JobInspectionSummary job)
      => DashboardJobFilterQuery.HandlerHref(DashboardFormat.ShortHandler(job));

    private static string HandlerFilterTitle(JobInspectionSummary job)
      => string.Concat("Filter jobs by handler ", DashboardFormat.ShortHandler(job));

    private static string TagFilterHref(string tag)
      => DashboardJobFilterQuery.TagHref(tag);
}
