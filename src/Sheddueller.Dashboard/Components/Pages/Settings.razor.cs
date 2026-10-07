namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;

using Sheddueller;
using Sheddueller.Storage;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dashboard operation failures are displayed without terminating the rendering circuit.")]
public partial class Settings
{
    private IShedduellerCleanupConfigurationStore? _store;
    private SettingsFormModel _form = new();
    private string? _loadError;
    private string? _actionMessage;
    private bool _isLoading;
    private bool _isSaving;
    private bool _isActionError;

    private string SaveButtonText
      => this._isSaving ? "Saving..." : "Save Settings";

    private string ActionAlertClass
      => this._isActionError
        ? "settings-inline-alert settings-inline-alert--error"
        : "settings-inline-alert settings-inline-alert--success";

    private string ActionAlertIcon
      => this._isActionError ? "warning" : "check_circle";

    protected override async Task OnInitializedAsync()
    {
        this._store = this.Services.GetService<IShedduellerCleanupConfigurationStore>();
        this._form = SettingsFormModel.FromConfiguration(this.CreateDefaultConfiguration());

        if (this._store is not null)
        {
            await this.LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (this._store is null)
        {
            return;
        }

        this._isLoading = true;
        this._loadError = null;
        this._actionMessage = null;

        try
        {
            var configuration = await this._store.GetCleanupConfigurationAsync(this.CreateDefaultConfiguration());
            this._form = SettingsFormModel.FromConfiguration(configuration);
        }
        catch (Exception exception)
        {
            this._loadError = exception.Message;
        }
        finally
        {
            this._isLoading = false;
        }
    }

    private async Task SaveAsync()
    {
        if (this._store is null || this._isSaving)
        {
            return;
        }

        this._isSaving = true;
        this._actionMessage = null;
        this._isActionError = false;

        try
        {
            var configuration = this._form.ToConfiguration();
            await this._store.SetCleanupConfigurationAsync(configuration);
            this._form = SettingsFormModel.FromConfiguration(configuration);
            this._actionMessage = "Cleanup settings saved.";
        }
        catch (Exception exception)
        {
            this._isActionError = true;
            this._actionMessage = string.Concat("Save failed: ", exception.Message);
        }
        finally
        {
            this._isSaving = false;
        }
    }

    private ShedduellerCleanupConfiguration CreateDefaultConfiguration()
      => new(
        JobRetentionCleanupConfiguration.FromOptions(this.ShedduellerOptions.Value.JobRetention),
        new JobEventCleanupConfiguration(this.DashboardOptions.Value.EventRetention, JobEventCleanupConfiguration.DefaultCleanupInterval),
        MetricsCleanupConfiguration.Default);

    private sealed class SettingsFormModel
    {
        public bool JobRetentionEnabled { get; set; }

        public int CompletedRetentionHours { get; set; }

        public bool CompletedRetainForever { get; set; }

        public int FailedRetentionHours { get; set; }

        public bool FailedRetainForever { get; set; }

        public int CanceledRetentionHours { get; set; }

        public bool CanceledRetainForever { get; set; }

        public int JobRetentionCleanupIntervalMinutes { get; set; }

        public int JobRetentionBatchSize { get; set; }

        public int JobEventRetentionHours { get; set; }

        public int JobEventCleanupIntervalMinutes { get; set; }

        public int MetricsRetentionHours { get; set; }

        public int MetricsCleanupIntervalMinutes { get; set; }

        public static SettingsFormModel FromConfiguration(ShedduellerCleanupConfiguration configuration)
          => new()
          {
              JobRetentionEnabled = configuration.JobRetention.Enabled,
              CompletedRetentionHours = ToPositiveHours(configuration.JobRetention.CompletedRetention ?? TimeSpan.FromDays(1)),
              CompletedRetainForever = configuration.JobRetention.CompletedRetention is null,
              FailedRetentionHours = ToPositiveHours(configuration.JobRetention.FailedRetention ?? TimeSpan.FromDays(7)),
              FailedRetainForever = configuration.JobRetention.FailedRetention is null,
              CanceledRetentionHours = ToPositiveHours(configuration.JobRetention.CanceledRetention ?? TimeSpan.FromDays(7)),
              CanceledRetainForever = configuration.JobRetention.CanceledRetention is null,
              JobRetentionCleanupIntervalMinutes = ToPositiveMinutes(configuration.JobRetention.CleanupInterval),
              JobRetentionBatchSize = configuration.JobRetention.BatchSize,
              JobEventRetentionHours = ToPositiveHours(configuration.JobEvents.Retention),
              JobEventCleanupIntervalMinutes = ToPositiveMinutes(configuration.JobEvents.CleanupInterval),
              MetricsRetentionHours = ToPositiveHours(configuration.Metrics.Retention),
              MetricsCleanupIntervalMinutes = ToPositiveMinutes(configuration.Metrics.CleanupInterval),
          };

        public ShedduellerCleanupConfiguration ToConfiguration()
          => new(
            new JobRetentionCleanupConfiguration(
              this.JobRetentionEnabled,
              this.CompletedRetainForever ? null : ToPositiveTimeSpanFromHours(this.CompletedRetentionHours, "Completed retention"),
              this.FailedRetainForever ? null : ToPositiveTimeSpanFromHours(this.FailedRetentionHours, "Failed retention"),
              this.CanceledRetainForever ? null : ToPositiveTimeSpanFromHours(this.CanceledRetentionHours, "Canceled retention"),
              ToPositiveTimeSpanFromMinutes(this.JobRetentionCleanupIntervalMinutes, "Terminal job cleanup interval"),
              RequirePositive(this.JobRetentionBatchSize, "Batch size")),
            new JobEventCleanupConfiguration(
              ToPositiveTimeSpanFromHours(this.JobEventRetentionHours, "Job event retention"),
              ToPositiveTimeSpanFromMinutes(this.JobEventCleanupIntervalMinutes, "Job event cleanup interval")),
            new MetricsCleanupConfiguration(
              ToPositiveTimeSpanFromHours(this.MetricsRetentionHours, "Metrics retention"),
              ToPositiveTimeSpanFromMinutes(this.MetricsCleanupIntervalMinutes, "Metrics cleanup interval")));

        private static int ToPositiveHours(TimeSpan value)
          => Math.Max(1, Convert.ToInt32(Math.Ceiling(value.TotalHours)));

        private static int ToPositiveMinutes(TimeSpan value)
          => Math.Max(1, Convert.ToInt32(Math.Ceiling(value.TotalMinutes)));

        private static TimeSpan ToPositiveTimeSpanFromHours(
            int value,
            string label)
          => TimeSpan.FromHours(RequirePositive(value, label));

        private static TimeSpan ToPositiveTimeSpanFromMinutes(
            int value,
            string label)
          => TimeSpan.FromMinutes(RequirePositive(value, label));

        private static int RequirePositive(
            int value,
            string label)
        {
            if (value <= 0)
            {
                throw new InvalidOperationException(string.Concat(label, " must be positive."));
            }

            return value;
        }
    }
}
