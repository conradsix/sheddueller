namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;

using Sheddueller.Inspection.Metrics;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dashboard operation failures are displayed without terminating the rendering circuit.")]
[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Preserve the existing public Razor component name.")]
public partial class Metrics
{
    private static readonly TimeSpan PageRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] DefaultMetricWindows = [TimeSpan.FromMinutes(5), TimeSpan.FromHours(1),
TimeSpan.FromHours(24)];

    private MetricsInspectionSnapshot? _snapshot;
    private string? _loadError;
    private TimeSpan _selectedWindow = TimeSpan.FromHours(1);
    private bool _isLoading;
    private bool _metricsUnsupported;

    private IReadOnlyList<MetricsInspectionWindow> Windows
      => this._snapshot?.Windows ?? [];

    private MetricsInspectionWindow? SelectedWindow
      => this.Windows.FirstOrDefault(window => window.Window == this._selectedWindow)
        ?? (this.Windows.Count > 0 ? this.Windows[0] : null);

    protected override async Task OnInitializedAsync()
    {
        this.InitializeLiveRefresh(
          this.RefreshCurrentQueryAsync,
          showRefreshing: false,
          lastUpdatedUtc: nowUtc => this.LiveRefresh.LastUpdatedUtc ?? (this._snapshot is null ? null : nowUtc));

        await this.LoadAsync();
        this.StartPeriodicRefresh(PageRefreshInterval);
    }

    private void SetSelectedWindow(TimeSpan window)
      => this._selectedWindow = window;

    private async Task LoadAsync()
    {
        this._loadError = null;
        this._metricsUnsupported = false;
        this.LiveRefresh.ClearError();
        this._isLoading = true;

        try
        {
            var snapshot = await this.Reader.GetMetricsAsync(
              new MetricsInspectionQuery(DefaultMetricWindows),
              CancellationToken.None);
            this.ApplySnapshot(snapshot);
            this.LiveRefresh.MarkUpdated();
        }
        catch (NotSupportedException)
        {
            this._metricsUnsupported = true;
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

    private async Task RefreshCurrentQueryAsync(CancellationToken cancellationToken)
    {
        var snapshot = await this.Reader.GetMetricsAsync(
          new MetricsInspectionQuery(DefaultMetricWindows),
          cancellationToken);
        await this.ApplyPageStateAsync(() => this.ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(MetricsInspectionSnapshot snapshot)
    {
        this._snapshot = snapshot;
        this._loadError = null;
        this._metricsUnsupported = false;

        if (snapshot.Windows.Count > 0 && !snapshot.Windows.Any(window => window.Window == this._selectedWindow))
        {
            this._selectedWindow = snapshot.Windows[0].Window;
        }
    }

    private string WindowRowClass(MetricsInspectionWindow window)
      => window.Window == (this.SelectedWindow?.Window ?? this._selectedWindow) ? "metrics-row--selected" : string.Empty;
}
