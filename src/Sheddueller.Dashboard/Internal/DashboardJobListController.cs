namespace Sheddueller.Dashboard.Internal;

using System.Diagnostics.CodeAnalysis;

using Sheddueller.Inspection.Jobs;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "List operation errors are exposed to the dashboard without terminating its circuit.")]
internal sealed class DashboardJobListController(
    IJobInspectionReader reader,
    int pageSize,
    Action clearRefreshError,
    Action markUpdated,
    Action notifyChanged,
    Func<Action, Task> applyPageStateAsync)
{
    private readonly IJobInspectionReader _reader = reader;
    private int _queryVersion;

    public List<JobInspectionSummary> Jobs { get; } = [];

    public JobInspectionPage? Page { get; private set; }

    public JobInspectionQuery LastQuery { get; private set; } = new(PageSize: pageSize);

    public string? Error { get; private set; }

    public bool IsLoading { get; private set; }

    public bool IsLoadingMore { get; private set; }

    public async Task LoadAsync(JobInspectionQuery query)
    {
        var queryVersion = ++this._queryVersion;
        this.LastQuery = query;
        this.Error = null;
        clearRefreshError();
        this.IsLoading = true;

        try
        {
            var page = await this._reader.SearchJobsAsync(query);
            if (queryVersion != this._queryVersion)
            {
                return;
            }

            this.ReplaceJobs(page);
            markUpdated();
        }
        catch (Exception exception)
        {
            if (queryVersion == this._queryVersion)
            {
                this.Error = exception.Message;
            }
        }
        finally
        {
            if (queryVersion == this._queryVersion)
            {
                this.IsLoading = false;
                notifyChanged();
            }
        }
    }

    public async Task LoadMoreAsync()
    {
        if (this.Page?.ContinuationToken is null || this.IsLoading || this.IsLoadingMore)
        {
            return;
        }

        var queryVersion = this._queryVersion;
        this.Error = null;
        this.IsLoadingMore = true;

        try
        {
            var page = await this._reader.SearchJobsAsync(this.LastQuery with
            {
                PageSize = pageSize,
                ContinuationToken = this.Page.ContinuationToken,
            });

            if (queryVersion != this._queryVersion)
            {
                return;
            }

            this.Jobs.AddRange(page.Jobs);
            this.Page = page;
            markUpdated();
        }
        catch (Exception exception)
        {
            if (queryVersion == this._queryVersion)
            {
                this.Error = exception.Message;
            }
        }
        finally
        {
            if (queryVersion == this._queryVersion)
            {
                this.IsLoadingMore = false;
                notifyChanged();
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var queryVersion = this._queryVersion;
        var visiblePageSize = Math.Max(pageSize, this.Jobs.Count);
        var page = await this._reader.SearchJobsAsync(this.LastQuery with
        {
            PageSize = visiblePageSize,
            ContinuationToken = null,
        }, cancellationToken);

        if (queryVersion != this._queryVersion)
        {
            return;
        }

        await applyPageStateAsync(() => this.ReplaceJobs(page));
    }

    private void ReplaceJobs(JobInspectionPage page)
    {
        this.Jobs.Clear();
        this.Jobs.AddRange(page.Jobs);
        this.Page = page;
    }
}
