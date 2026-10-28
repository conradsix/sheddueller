namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

/// <content>
/// The job's event log, read a page at a time and extended by live events. It feeds the Logs and Timeline tabs.
/// </content>
public partial class JobDetail
{
    private const int EventPageSize = 500;

    private readonly DashboardJobEventLog _eventLog = new(EventPageSize);
    private bool _isLoadingMoreEvents;

    private bool HasMoreEvents
      => this._detail is not null && this._eventLog.HasMoreToRead;

    private string LoadMoreEventsText
      => this._isLoadingMoreEvents
        ? "Loading events…"
        : string.Create(CultureInfo.InvariantCulture, $"Load more events ({DashboardFormat.Count(this._eventLog.Events.Count)} loaded)");

    // The Timeline tab's entries.
    private IReadOnlyList<JobEvent> LifecycleEvents
      => [.. this._eventLog.Events.Where(DashboardJobDetailFormat.IsTimelineEvent)];

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed read is shown as a toast; it must not escape the event handler and end the circuit.")]
    private async Task LoadMoreEventsAsync()
    {
        if (this._isLoadingMoreEvents || !this.HasMoreEvents)
        {
            return;
        }

        var jobId = this.JobId;
        this._isLoadingMoreEvents = true;

        try
        {
            var events = await this.ReadEventPageAsync(jobId, this._eventLog.ReadThroughSequence, CancellationToken.None);
            if (jobId == this.JobId)
            {
                _ = this._eventLog.AppendPage(events);
            }
        }
        catch (Exception exception)
        {
            if (jobId == this.JobId)
            {
                this.ShowToast("Couldn't load more events", exception.Message, AlertTone.Destructive);
            }
        }
        finally
        {
            if (jobId == this.JobId)
            {
                this._isLoadingMoreEvents = false;
            }
        }
    }

    private async Task<IReadOnlyList<JobEvent>> ReadEventPageAsync(
        Guid jobId,
        long? afterEventSequence,
        CancellationToken cancellationToken)
    {
        var events = new List<JobEvent>();
        await foreach (var jobEvent in this.Reader.ReadEventsAsync(
            jobId,
            new JobEventReadOptions(afterEventSequence, EventPageSize),
            cancellationToken))
        {
            events.Add(jobEvent);
        }

        return events;
    }
}
