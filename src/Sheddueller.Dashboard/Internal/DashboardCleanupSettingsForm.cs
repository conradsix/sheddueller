namespace Sheddueller.Dashboard.Internal;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

/// <summary>
/// The editable values on the settings page.
/// </summary>
internal enum DashboardCleanupSetting
{
    CompletedRetention,
    FailedRetention,
    CanceledRetention,
    JobCleanupInterval,
    JobBatchSize,
    EventRetention,
    EventCleanupInterval,
    MetricsRetention,
    MetricsCleanupInterval,
    GroupIdleRetention,
    GroupCleanupInterval,
    GroupBatchSize,
}

/// <summary>
/// Working copy of the cluster cleanup configuration while it is edited: keeps what was typed, validates each value,
/// and tells whether anything differs from the loaded configuration.
/// </summary>
internal sealed class DashboardCleanupSettingsForm
{
    private const NumberStyles BatchSizeStyles = NumberStyles.AllowLeadingWhite
      | NumberStyles.AllowTrailingWhite
      | NumberStyles.AllowLeadingSign
      | NumberStyles.AllowThousands;

    // Shown in a retention field while "Keep forever" is ticked, so unticking it starts from the usual default.
    private static readonly TimeSpan DefaultCompletedRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan DefaultFailedRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan DefaultCanceledRetention = TimeSpan.FromDays(7);

    private readonly Dictionary<DashboardCleanupSetting, DashboardDurationInput> _durations = [];
    private readonly HashSet<DashboardCleanupSetting> _keptForever = [];

    private DashboardCleanupSettingsForm()
    {
    }

    public static IReadOnlyList<DashboardCleanupSetting> Settings { get; } = Enum.GetValues<DashboardCleanupSetting>();

    public bool JobRetentionEnabled { get; set; }

    public string BatchSize { get; set; } = string.Empty;

    public bool GroupCleanupEnabled { get; set; }

    public string GroupBatchSize { get; set; } = string.Empty;

    /// <summary>
    /// Settings whose current value can't be saved.
    /// </summary>
    public IReadOnlyList<DashboardCleanupSetting> InvalidSettings
      => [.. Settings.Where(setting => this.Error(setting) is not null)];

    public static DashboardCleanupSettingsForm FromConfiguration(ShedduellerCleanupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var jobRetention = configuration.JobRetention;
        var form = new DashboardCleanupSettingsForm
        {
            JobRetentionEnabled = jobRetention.Enabled,
            GroupCleanupEnabled = configuration.ConcurrencyGroups.Enabled,
            GroupBatchSize = configuration.ConcurrencyGroups.BatchSize.ToString(CultureInfo.InvariantCulture),
            BatchSize = jobRetention.BatchSize.ToString(CultureInfo.InvariantCulture),
        };

        form.SetRetention(DashboardCleanupSetting.CompletedRetention, jobRetention.CompletedRetention, DefaultCompletedRetention);
        form.SetRetention(DashboardCleanupSetting.FailedRetention, jobRetention.FailedRetention, DefaultFailedRetention);
        form.SetRetention(DashboardCleanupSetting.CanceledRetention, jobRetention.CanceledRetention, DefaultCanceledRetention);
        form.SetDurationValue(DashboardCleanupSetting.JobCleanupInterval, jobRetention.CleanupInterval);
        form.SetDurationValue(DashboardCleanupSetting.EventRetention, configuration.JobEvents.Retention);
        form.SetDurationValue(DashboardCleanupSetting.EventCleanupInterval, configuration.JobEvents.CleanupInterval);
        form.SetDurationValue(DashboardCleanupSetting.MetricsRetention, configuration.Metrics.Retention);
        form.SetDurationValue(DashboardCleanupSetting.MetricsCleanupInterval, configuration.Metrics.CleanupInterval);

        form.SetDurationValue(DashboardCleanupSetting.GroupIdleRetention, configuration.ConcurrencyGroups.IdleRetention);
        form.SetDurationValue(DashboardCleanupSetting.GroupCleanupInterval, configuration.ConcurrencyGroups.CleanupInterval);
        return form;
    }

    /// <summary>
    /// Whether the setting is a retention that can be switched off with "Keep forever".
    /// </summary>
    public static bool CanKeepForever(DashboardCleanupSetting setting)
      => setting is DashboardCleanupSetting.CompletedRetention
        or DashboardCleanupSetting.FailedRetention
        or DashboardCleanupSetting.CanceledRetention;

    public DashboardDurationInput Duration(DashboardCleanupSetting setting)
      => this._durations.TryGetValue(setting, out var duration)
        ? duration
        : throw new ArgumentOutOfRangeException(nameof(setting), setting, "The setting is not a duration.");

    public void SetDuration(
        DashboardCleanupSetting setting,
        DashboardDurationInput duration)
    {
        ArgumentNullException.ThrowIfNull(duration);
        _ = this.Duration(setting);
        this._durations[setting] = duration;
    }

    public bool IsKeptForever(DashboardCleanupSetting setting)
      => this._keptForever.Contains(setting);

    public void SetKeptForever(
        DashboardCleanupSetting setting,
        bool keepForever)
    {
        if (!CanKeepForever(setting))
        {
            throw new ArgumentOutOfRangeException(nameof(setting), setting, "Only job retentions can be kept forever.");
        }

        _ = keepForever ? this._keptForever.Add(setting) : this._keptForever.Remove(setting);
    }

    /// <summary>
    /// Why the setting's current value can't be saved, or <see langword="null"/> when it can.
    /// </summary>
    public string? Error(DashboardCleanupSetting setting)
    {
        if (setting is DashboardCleanupSetting.JobBatchSize or DashboardCleanupSetting.GroupBatchSize)
        {
            return TryParseBatchSize(setting == DashboardCleanupSetting.JobBatchSize ? this.BatchSize : this.GroupBatchSize, out _, out var batchSizeError) ? null : batchSizeError;
        }

        return this.IsKeptForever(setting) || this.Duration(setting).TryGetValue(out _, out var error)
          ? null
          : error;
    }

    /// <summary>
    /// Builds the configuration to save, when every value is valid.
    /// </summary>
    public bool TryCreateConfiguration([NotNullWhen(true)] out ShedduellerCleanupConfiguration? configuration)
    {
        configuration = null;
        if (!TryParseBatchSize(this.BatchSize, out var batchSize, out _)
            || !TryParseBatchSize(this.GroupBatchSize, out var groupBatchSize, out _)
            || !this.TryGetDuration(DashboardCleanupSetting.GroupIdleRetention, out var groupIdleRetention)
            || !this.TryGetDuration(DashboardCleanupSetting.GroupCleanupInterval, out var groupCleanupInterval)
            || !this.TryGetRetention(DashboardCleanupSetting.CompletedRetention, out var completedRetention)
            || !this.TryGetRetention(DashboardCleanupSetting.FailedRetention, out var failedRetention)
            || !this.TryGetRetention(DashboardCleanupSetting.CanceledRetention, out var canceledRetention)
            || !this.TryGetDuration(DashboardCleanupSetting.JobCleanupInterval, out var jobCleanupInterval)
            || !this.TryGetDuration(DashboardCleanupSetting.EventRetention, out var eventRetention)
            || !this.TryGetDuration(DashboardCleanupSetting.EventCleanupInterval, out var eventCleanupInterval)
            || !this.TryGetDuration(DashboardCleanupSetting.MetricsRetention, out var metricsRetention)
            || !this.TryGetDuration(DashboardCleanupSetting.MetricsCleanupInterval, out var metricsCleanupInterval))
        {
            return false;
        }

        configuration = new ShedduellerCleanupConfiguration(
          new JobRetentionCleanupConfiguration(
            this.JobRetentionEnabled,
            completedRetention,
            failedRetention,
            canceledRetention,
            jobCleanupInterval,
            batchSize),
          new JobEventCleanupConfiguration(eventRetention, eventCleanupInterval),
          new MetricsCleanupConfiguration(metricsRetention, metricsCleanupInterval))
        {
            ConcurrencyGroups = new(this.GroupCleanupEnabled, groupIdleRetention, groupCleanupInterval, groupBatchSize),
        };
        return true;
    }

    /// <summary>
    /// Whether any value differs from <paramref name="baseline"/>. Values are compared by what they mean, so
    /// <c>7 days</c> and <c>168 hours</c> are the same; text that can't be read is compared as typed.
    /// </summary>
    public bool HasChangesFrom(DashboardCleanupSettingsForm baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        return this.JobRetentionEnabled != baseline.JobRetentionEnabled
          || this.GroupCleanupEnabled != baseline.GroupCleanupEnabled
          || Settings.Any(setting => !this.SettingEquals(baseline, setting));
    }

    internal static bool TryParseBatchSize(
        string? text,
        out int value,
        [NotNullWhen(false)] out string? error)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a batch size.";
            return false;
        }

        if (!long.TryParse(text, BatchSizeStyles, CultureInfo.InvariantCulture, out var parsed))
        {
            error = "Enter a whole number, such as 1000.";
            return false;
        }

        if (parsed < 1)
        {
            error = "Must be at least 1.";
            return false;
        }

        if (parsed > int.MaxValue)
        {
            error = "Must be 2,147,483,647 or less.";
            return false;
        }

        value = (int)parsed;
        error = null;
        return true;
    }

    private void SetRetention(
        DashboardCleanupSetting setting,
        TimeSpan? retention,
        TimeSpan placeholder)
    {
        this.SetDurationValue(setting, retention ?? placeholder);
        this.SetKeptForever(setting, retention is null);
    }

    private void SetDurationValue(
        DashboardCleanupSetting setting,
        TimeSpan value)
      => this._durations[setting] = DashboardDurationInput.FromTimeSpan(value);

    private bool TryGetDuration(
        DashboardCleanupSetting setting,
        out TimeSpan value)
      => this.Duration(setting).TryGetValue(out value, out _);

    private bool TryGetRetention(
        DashboardCleanupSetting setting,
        out TimeSpan? value)
    {
        value = null;
        if (this.IsKeptForever(setting))
        {
            return true;
        }

        if (!this.TryGetDuration(setting, out var retention))
        {
            return false;
        }

        value = retention;
        return true;
    }

    private bool SettingEquals(
        DashboardCleanupSettingsForm other,
        DashboardCleanupSetting setting)
    {
        if (setting is DashboardCleanupSetting.JobBatchSize or DashboardCleanupSetting.GroupBatchSize)
        {
            var text = setting == DashboardCleanupSetting.JobBatchSize ? this.BatchSize : this.GroupBatchSize;
            var otherText = setting == DashboardCleanupSetting.JobBatchSize ? other.BatchSize : other.GroupBatchSize;
            return TryParseBatchSize(text, out var batchSize, out _)
              && TryParseBatchSize(otherText, out var otherBatchSize, out _)
              ? batchSize == otherBatchSize
              : string.Equals(text.Trim(), otherText.Trim(), StringComparison.Ordinal);
        }

        var keptForever = this.IsKeptForever(setting);
        if (keptForever != other.IsKeptForever(setting))
        {
            return false;
        }

        if (keptForever)
        {
            return true;
        }

        var duration = this.Duration(setting);
        var otherDuration = other.Duration(setting);
        return duration.TryGetValue(out var value, out _) && otherDuration.TryGetValue(out var otherValue, out _)
          ? value == otherValue
          : duration.Unit == otherDuration.Unit
            && string.Equals(duration.Amount.Trim(), otherDuration.Amount.Trim(), StringComparison.Ordinal);
    }
}
