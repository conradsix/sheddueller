namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.AspNetCore.Components;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Nodes;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dashboard operation failures are displayed without terminating the rendering circuit.")]
[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Preserve the existing public Razor component name.")]
public partial class Nodes
{
    private static readonly TimeSpan PageRefreshInterval = TimeSpan.FromSeconds(5);
    private const int DefaultPageSize = 25;

    private readonly List<NodeInspectionSummary> _nodes = [];
    private readonly DashboardNodeFilters _filters = new();
    private NodeInspectionPage? _nodePage;
    private NodeHealthCounts? _healthCounts;
    private string? _loadError;
    private bool _isLoading;
    private bool _isLoadingMore;

    private IReadOnlyList<NodeInspectionSummary> FilteredNodes
      => this._filters.ApplyClientFilter(this._nodes);

    private int ActiveNodeCount
      => this._healthCounts?.ActiveNodeCount ?? this._nodes.Count(node => node.State == NodeHealthState.Active);

    private int StaleNodeCount
      => this._healthCounts?.StaleNodeCount ?? this._nodes.Count(node => node.State == NodeHealthState.Stale);

    private int DeadNodeCount
      => this._healthCounts?.DeadNodeCount ?? this._nodes.Count(node => node.State == NodeHealthState.Dead);

    private int TotalNodeCount
      => this._healthCounts?.TotalNodeCount ?? (this.ActiveNodeCount + this.StaleNodeCount + this.DeadNodeCount);

    private string EmptyText
      => this._nodes.Count == 0
        ? "No worker nodes matched the current query."
        : "No loaded worker nodes matched the node ID filter.";

    private string ResultRangeText
    {
        get
        {
            var loadedCount = this._nodes.Count;
            var visibleCount = this.FilteredNodes.Count;
            var totalCount = this._nodePage?.TotalCount ?? Math.Max(this.TotalNodeCount, loadedCount);
            var filteredText = visibleCount == loadedCount
              ? string.Empty
              : string.Create(CultureInfo.InvariantCulture, $" ({DashboardFormat.Count(visibleCount)} visible after node ID filter)");

            return loadedCount == 0
              ? string.Create(CultureInfo.InvariantCulture, $"Showing 0 of {DashboardFormat.Count(totalCount)} nodes{filteredText}")
              : string.Create(CultureInfo.InvariantCulture, $"Showing 1-{DashboardFormat.Count(loadedCount)} of {DashboardFormat.Count(totalCount)} nodes{filteredText}");
        }
    }

    protected override async Task OnInitializedAsync()
    {
        this.InitializeLiveRefresh(
          this.RefreshCurrentQueryAsync,
          showRefreshing: false,
          lastUpdatedUtc: nowUtc => this.LiveRefresh.LastUpdatedUtc ?? (this._nodePage is null ? null : nowUtc));

        await this.LoadAsync();
        this.StartPeriodicRefresh(PageRefreshInterval);
    }

    private async Task SetStateFilterAsync(ChangeEventArgs eventArgs)
    {
        this._filters.State = eventArgs.Value?.ToString() ?? string.Empty;
        this._filters.NodeId = string.Empty;

        await this.LoadAsync();
    }

    private void SetNodeIdFilter(string value)
      => this._filters.NodeId = value;

    private async Task LoadAsync()
    {
        this._loadError = null;
        this.LiveRefresh.ClearError();
        this._isLoading = true;

        try
        {
            var snapshot = await this.ReadSnapshotAsync(
              continuationToken: null,
              pageSize: DefaultPageSize,
              CancellationToken.None)
              ;
            this.ApplySnapshot(snapshot, append: false);
            this.LiveRefresh.MarkUpdated();
        }
        catch (Exception exception)
        {
            this._loadError = exception.Message;
        }
        finally
        {
            this._isLoading = false;
            this.NotifyShellRefreshChanged();
        }
    }

    private async Task LoadMoreAsync()
    {
        if (this._nodePage?.ContinuationToken is null || this._isLoadingMore)
        {
            return;
        }

        this._loadError = null;
        this._isLoadingMore = true;

        try
        {
            var snapshot = await this.ReadSnapshotAsync(
              this._nodePage.ContinuationToken,
              DefaultPageSize,
              CancellationToken.None)
              ;
            this.ApplySnapshot(snapshot, append: true);
            this.LiveRefresh.MarkUpdated();
        }
        catch (Exception exception)
        {
            this._loadError = exception.Message;
        }
        finally
        {
            this._isLoadingMore = false;
            this.NotifyShellRefreshChanged();
        }
    }

    private async Task RefreshCurrentQueryAsync(CancellationToken cancellationToken)
    {
        var visiblePageSize = Math.Max(DefaultPageSize, this._nodes.Count);
        var snapshot = await this.ReadSnapshotAsync(
          continuationToken: null,
          pageSize: visiblePageSize,
          cancellationToken)
          ;

        await this.ApplyPageStateAsync(() => this.ApplySnapshot(snapshot, append: false));
    }

    [SuppressMessage("Usage", "CA1849:Call async methods when in an async method", Justification = "All result tasks have completed successfully after the preceding Task.WhenAll.")]
    private async Task<NodesSnapshot> ReadSnapshotAsync(
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var nodePageTask = this.NodeReader.SearchNodesAsync(
          this._filters.ToQuery(pageSize, continuationToken),
          cancellationToken)
          .AsTask();
        var activeCountTask = this.ReadNodeHealthCountAsync(NodeHealthState.Active, cancellationToken);
        var staleCountTask = this.ReadNodeHealthCountAsync(NodeHealthState.Stale, cancellationToken);
        var deadCountTask = this.ReadNodeHealthCountAsync(NodeHealthState.Dead, cancellationToken);

        await Task.WhenAll(nodePageTask, activeCountTask, staleCountTask, deadCountTask);

        return new NodesSnapshot(
          nodePageTask.Result,
          new NodeHealthCounts(
            activeCountTask.Result,
            staleCountTask.Result,
            deadCountTask.Result));
    }

    private async Task<int> ReadNodeHealthCountAsync(
        NodeHealthState state,
        CancellationToken cancellationToken)
    {
        var page = await this.NodeReader.SearchNodesAsync(
          new NodeInspectionQuery(State: state, PageSize: 1),
          cancellationToken)
          .ConfigureAwait(false);

        return Convert.ToInt32(page.TotalCount, CultureInfo.InvariantCulture);
    }

    private void ApplySnapshot(
        NodesSnapshot snapshot,
        bool append)
    {
        if (!append)
        {
            this._nodes.Clear();
        }

        this._nodes.AddRange(snapshot.Page.Nodes);
        this._nodePage = snapshot.Page;
        this._healthCounts = snapshot.HealthCounts;
        this._loadError = null;
    }

    private string HealthPercent(int count)
      => this.TotalNodeCount == 0
        ? "0%"
        : string.Create(CultureInfo.InvariantCulture, $"{(double)count / this.TotalNodeCount * 100:0.#}%");

    private static string NodeRowClass(NodeInspectionSummary node)
      => node.State switch
      {
          NodeHealthState.Stale => "nodes-row--stale",
          NodeHealthState.Dead => "nodes-row--dead",
          _ => string.Empty,
      };

    private static string NodeStateClass(NodeHealthState state)
      => string.Concat("nodes-state nodes-state--", state.ToString().ToLowerInvariant());

    private static string ConcurrencyText(NodeInspectionSummary node)
      => string.Create(CultureInfo.InvariantCulture, $"{DashboardFormat.Count(node.CurrentExecutionCount)} / {DashboardFormat.Count(node.MaxConcurrentExecutionsPerNode)}");

    private static string ConcurrencyWidthStyle(NodeInspectionSummary node)
    {
        var percent = node.MaxConcurrentExecutionsPerNode <= 0
          ? 0
          : Math.Clamp((double)node.CurrentExecutionCount / node.MaxConcurrentExecutionsPerNode * 100, 0, 100);
        return string.Create(CultureInfo.InvariantCulture, $"width: {percent:0.##}%");
    }

    private static string ConcurrencyBarClass(NodeInspectionSummary node)
    {
        if (node.CurrentExecutionCount <= 0 || node.State != NodeHealthState.Active)
        {
            return "nodes-concurrency__bar nodes-concurrency__bar--inactive";
        }

        var usage = node.MaxConcurrentExecutionsPerNode <= 0
          ? 0
          : (double)node.CurrentExecutionCount / node.MaxConcurrentExecutionsPerNode;
        return usage >= 0.8
          ? "nodes-concurrency__bar nodes-concurrency__bar--hot"
          : "nodes-concurrency__bar";
    }

    private sealed record NodesSnapshot(
        NodeInspectionPage Page,
        NodeHealthCounts HealthCounts);

    private sealed record NodeHealthCounts(
        int ActiveNodeCount,
        int StaleNodeCount,
        int DeadNodeCount)
    {
        public int TotalNodeCount
          => this.ActiveNodeCount + this.StaleNodeCount + this.DeadNodeCount;
    }
}
