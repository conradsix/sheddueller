namespace Sheddueller.Dashboard.Components;

using System.Diagnostics.CodeAnalysis;

using Sheddueller.Dashboard.Internal;

[SuppressMessage("Design", "CA1063:Implement IDisposable Correctly", Justification = "Preserve the existing public component disposal contract.")]
[SuppressMessage("Usage", "CA1816:Dispose methods should call SuppressFinalize", Justification = "Preserve existing component disposal behavior.")]
public partial class DashboardLayout
{
    private readonly DashboardShellRefreshContext _refreshContext = new();
    private bool _disposed;

    private DashboardShellRefreshRegistration? CurrentRefresh
      => this._refreshContext.Current;

    protected override void OnInitialized() => this._refreshContext.Changed += this.OnRefreshContextChanged;

    public void Dispose()
    {
        this._disposed = true;
        this._refreshContext.Changed -= this.OnRefreshContextChanged;
    }

    private void OnRefreshContextChanged()
    {
        if (!this._disposed)
        {
            _ = this.InvokeAsync(this.StateHasChanged);
        }
    }

    private async Task ToggleShellAutoRefreshAsync()
      => await this._refreshContext.ToggleCurrentAutoRefreshAsync();

    private static string ShellRefreshButtonClass(DashboardShellRefreshRegistration refresh)
      => refresh.Coordinator.AutoRefreshEnabled
        ? "sd-shell-refresh__button sd-shell-refresh__button--active"
        : "sd-shell-refresh__button";

    private static string AutoRefreshStateText(DashboardShellRefreshRegistration refresh)
      => refresh.Coordinator.AutoRefreshEnabled ? "On" : "Off";
}
