namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Sheddueller;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.ConcurrencyGroups;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dashboard operation failures are displayed without terminating the rendering circuit.")]
[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Preserve the existing public Razor component name.")]
public partial class ConcurrencyGroups
{
    private static readonly TimeSpan PageRefreshInterval = TimeSpan.FromSeconds(5);
    private const int DefaultPageSize = 25;
    private const string SecondsUnit = "seconds";
    private const string MinutesUnit = "minutes";
    private const string HoursUnit = "hours";

    private readonly List<ConcurrencyGroupInspectionSummary> _groups = [];
    private readonly DashboardConcurrencyGroupFilters _filters = new();
    private IConcurrencyGroupManager? _manager;
    private ConcurrencyGroupInspectionPage? _page;
    private string? _loadError;
    private string? _actionMessage;
    private string? _editingGroupKey;
    private string? _editingRateGroupKey;
    private int _limitInput = 1;
    private int _ratePermitInput = 1;
    private decimal _ratePeriodInput = 1;
    private string _ratePeriodUnit = SecondsUnit;
    private bool _isLoading;
    private bool _isLoadingMore;
    private bool _isActionRunning;
    private bool _isActionError;

    private IReadOnlyList<ConcurrencyGroupInspectionSummary> FilteredGroups
      => this._filters.ApplyClientFilter(this._groups);

    private bool CanEditLimits
      => this._manager is not null;

    private int GroupTableColumnCount
      => this.CanEditLimits ? 11 : 10;

    private string ActionAlertClass
      => this._isActionError
        ? "groups-inline-alert groups-inline-alert--error"
        : "groups-inline-alert groups-inline-alert--success";

    private string ActionAlertIcon
      => this._isActionError ? "warning" : "check_circle";

    private bool IsActionDisabled
      => this._isActionRunning || this._isLoading || this._isLoadingMore;

    private string EmptyText
      => this._groups.Count == 0
        ? "No concurrency groups matched the current query."
        : "No loaded concurrency groups matched the group key filter.";

    private string ResultRangeText
    {
        get
        {
            var loadedCount = this._groups.Count;
            var visibleCount = this.FilteredGroups.Count;
            var totalCount = this._page?.TotalCount ?? loadedCount;
            var filteredText = visibleCount == loadedCount
              ? string.Empty
              : string.Create(CultureInfo.InvariantCulture, $" ({DashboardFormat.Count(visibleCount)} visible after group key filter)");
            var moreText = this._page?.ContinuationToken is null ? string.Empty : " with more available";

            return loadedCount == 0
              ? string.Create(CultureInfo.InvariantCulture, $"Showing 0 of {DashboardFormat.Count(totalCount)} groups{filteredText}")
              : string.Create(CultureInfo.InvariantCulture, $"Showing 1-{DashboardFormat.Count(loadedCount)} of {DashboardFormat.Count(totalCount)} groups{filteredText}{moreText}");
        }
    }

    protected override async Task OnInitializedAsync()
    {
        this._manager = this.Services.GetService<IConcurrencyGroupManager>();
        this.InitializeLiveRefresh(
          this.RefreshCurrentQueryAsync,
          showRefreshing: false,
          lastUpdatedUtc: nowUtc => this.LiveRefresh.LastUpdatedUtc ?? (this._page is null ? null : nowUtc));

        await this.LoadAsync();
        this.StartPeriodicRefresh(PageRefreshInterval);
    }

    private async Task SetSaturatedOnlyAsync(bool value)
    {
        this._filters.SaturatedOnly = value;
        this.ClearActionAlert();
        this.CancelEdit();

        await this.LoadAsync();
    }

    private async Task SetHasBlockedJobsOnlyAsync(bool value)
    {
        this._filters.HasBlockedJobsOnly = value;
        this.ClearActionAlert();
        this.CancelEdit();

        await this.LoadAsync();
    }

    private async Task SetRateLimitedOnlyAsync(bool value)
    {
        this._filters.RateLimitedOnly = value;
        this.ClearActionAlert();
        this.CancelEdit();

        await this.LoadAsync();
    }

    private void SetGroupKeyFilter(string value)
    {
        this._filters.GroupKey = value;
        this.ClearActionAlert();
    }

    private async Task LoadAsync()
    {
        this._loadError = null;
        this.LiveRefresh.ClearError();
        this._isLoading = true;

        try
        {
            var page = await this.Reader.SearchConcurrencyGroupsAsync(this._filters.ToQuery(DefaultPageSize, continuationToken: null));
            this.ReplaceGroups(page);
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
        if (this._page?.ContinuationToken is null || this._isLoadingMore)
        {
            return;
        }

        this._loadError = null;
        this._isLoadingMore = true;

        try
        {
            var page = await this.Reader.SearchConcurrencyGroupsAsync(this._filters.ToQuery(DefaultPageSize, this._page.ContinuationToken));
            this._groups.AddRange(page.Groups);
            this._page = page;
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
        var visiblePageSize = Math.Max(DefaultPageSize, this._groups.Count);
        var page = await this.Reader.SearchConcurrencyGroupsAsync(
          this._filters.ToQuery(visiblePageSize, continuationToken: null),
          cancellationToken)
          ;

        await this.ApplyPageStateAsync(() => this.ReplaceGroups(page));
    }

    private void ReplaceGroups(ConcurrencyGroupInspectionPage page)
    {
        this._groups.Clear();
        this._groups.AddRange(page.Groups);
        this._page = page;
        this._loadError = null;
        if (this._editingGroupKey is not null && page.Groups.All(group => !string.Equals(group.GroupKey, this._editingGroupKey, StringComparison.Ordinal)))
        {
            this.CancelEdit();
        }

        if (this._editingRateGroupKey is not null && page.Groups.All(group => !string.Equals(group.GroupKey, this._editingRateGroupKey, StringComparison.Ordinal)))
        {
            this.CancelEdit();
        }
    }

    private void StartEdit(ConcurrencyGroupInspectionSummary group)
    {
        if (this.IsActionDisabled)
        {
            return;
        }

        this.ClearActionAlert();
        this._editingRateGroupKey = null;
        this._editingGroupKey = group.GroupKey;
        this._limitInput = group.OverrideLimit ?? group.EffectiveLimit;
    }

    private void CancelEdit()
    {
        this._editingGroupKey = null;
        this._editingRateGroupKey = null;
    }

    private void StartRateEdit(ConcurrencyGroupInspectionSummary group)
    {
        if (this.IsActionDisabled)
        {
            return;
        }

        this.ClearActionAlert();
        this._editingGroupKey = null;
        this._editingRateGroupKey = group.GroupKey;
        var rateLimit = group.OverrideRateLimit ?? group.EffectiveRateLimit ?? new ConcurrencyGroupRateLimit(1, TimeSpan.FromSeconds(1));
        this._ratePermitInput = rateLimit.PermitCount;
        (this._ratePeriodInput, this._ratePeriodUnit) = ToRatePeriodInput(rateLimit.Period);
    }

    private async Task SaveLimitAsync(ConcurrencyGroupInspectionSummary group)
    {
        if (this._manager is null || this.IsActionDisabled || !this.IsEditing(group))
        {
            return;
        }

        if (this._limitInput <= 0)
        {
            this.SetActionFailure("Limit save failed: Limit must be positive.");
            return;
        }

        this._isActionRunning = true;
        this.ClearActionAlert();

        try
        {
            await this._manager.SetLimitAsync(group.GroupKey, this._limitInput);
            this.CancelEdit();
            this.SetActionSuccess(string.Create(CultureInfo.InvariantCulture, $"Concurrency group {group.GroupKey} override set to {DashboardFormat.Count(this._limitInput)}."));
            await this.LiveRefresh.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            this.SetActionFailure(string.Create(CultureInfo.InvariantCulture, $"Limit save failed: {exception.Message}"));
        }
        finally
        {
            this._isActionRunning = false;
        }
    }

    private async Task ResetLimitAsync(ConcurrencyGroupInspectionSummary group)
    {
        if (this._manager is null || this.IsActionDisabled)
        {
            return;
        }

        this._isActionRunning = true;
        this.ClearActionAlert();

        try
        {
            await this._manager.ClearLimitOverrideAsync(group.GroupKey);
            if (this.IsEditing(group))
            {
                this.CancelEdit();
            }

            this.SetActionSuccess(string.Create(CultureInfo.InvariantCulture, $"Concurrency group {group.GroupKey} override reset."));
            await this.LiveRefresh.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            this.SetActionFailure(string.Create(CultureInfo.InvariantCulture, $"Limit reset failed: {exception.Message}"));
        }
        finally
        {
            this._isActionRunning = false;
        }
    }

    private async Task SaveRateLimitAsync(ConcurrencyGroupInspectionSummary group)
    {
        if (this._manager is null || this.IsActionDisabled || !this.IsEditingRate(group))
        {
            return;
        }

        if (!this.TryCreateRateLimit(out var rateLimit, out var validationMessage))
        {
            this.SetActionFailure(string.Concat("Rate-limit save failed: ", validationMessage));
            return;
        }

        this._isActionRunning = true;
        this.ClearActionAlert();

        try
        {
            await this._manager.SetRateLimitAsync(group.GroupKey, rateLimit);
            this.CancelEdit();
            this.SetActionSuccess(string.Create(
              CultureInfo.InvariantCulture,
              $"Concurrency group {group.GroupKey} rate override set to {FormatRateLimit(rateLimit)}."));
            await this.LiveRefresh.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            this.SetActionFailure(string.Create(CultureInfo.InvariantCulture, $"Rate-limit save failed: {exception.Message}"));
        }
        finally
        {
            this._isActionRunning = false;
        }
    }

    private async Task SetUnlimitedRateLimitAsync(ConcurrencyGroupInspectionSummary group)
    {
        if (this._manager is null || this.IsActionDisabled)
        {
            return;
        }

        this._isActionRunning = true;
        this.ClearActionAlert();

        try
        {
            await this._manager.SetUnlimitedRateLimitAsync(group.GroupKey);
            this.CancelEdit();
            this.SetActionSuccess(string.Create(
              CultureInfo.InvariantCulture,
              $"Concurrency group {group.GroupKey} rate override set to unlimited."));
            await this.LiveRefresh.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            this.SetActionFailure(string.Create(CultureInfo.InvariantCulture, $"Unlimited rate override failed: {exception.Message}"));
        }
        finally
        {
            this._isActionRunning = false;
        }
    }

    private async Task ResetRateLimitAsync(ConcurrencyGroupInspectionSummary group)
    {
        if (this._manager is null || this.IsActionDisabled)
        {
            return;
        }

        this._isActionRunning = true;
        this.ClearActionAlert();

        try
        {
            await this._manager.ClearRateLimitOverrideAsync(group.GroupKey);
            this.CancelEdit();
            this.SetActionSuccess(string.Create(
              CultureInfo.InvariantCulture,
              $"Concurrency group {group.GroupKey} rate override reset."));
            await this.LiveRefresh.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            this.SetActionFailure(string.Create(CultureInfo.InvariantCulture, $"Rate-limit reset failed: {exception.Message}"));
        }
        finally
        {
            this._isActionRunning = false;
        }
    }

    private bool IsEditing(ConcurrencyGroupInspectionSummary group)
      => string.Equals(this._editingGroupKey, group.GroupKey, StringComparison.Ordinal);

    private bool IsEditingRate(ConcurrencyGroupInspectionSummary group)
      => string.Equals(this._editingRateGroupKey, group.GroupKey, StringComparison.Ordinal);

    private void ClearActionAlert()
    {
        this._actionMessage = null;
        this._isActionError = false;
    }

    private void SetActionSuccess(string message)
    {
        this._isActionError = false;
        this._actionMessage = message;
    }

    private void SetActionFailure(string message)
    {
        this._isActionError = true;
        this._actionMessage = message;
    }

    private static string GroupRowClass(ConcurrencyGroupInspectionSummary group)
      => group.IsSaturated
        ? "groups-row--saturated"
        : group.IsRateLimited ? "groups-row--rate-limited" : string.Empty;

    private static string BlockedCountClass(ConcurrencyGroupInspectionSummary group)
      => group.BlockedJobCount > 0 ? "groups-blocked-count groups-blocked-count--attention" : "groups-blocked-count";

    private static string SaturationStateText(ConcurrencyGroupInspectionSummary group)
    {
        if (group.IsSaturated)
        {
            return "Saturated";
        }

        if (group.IsRateLimited)
        {
            return "Rate Limited";
        }

        if (group.BlockedJobCount > 0)
        {
            return "Blocked Work";
        }

        return GetOccupancyRatio(group) >= 0.8 ? "High Load" : "Nominal";
    }

    private static string SaturationStateClass(ConcurrencyGroupInspectionSummary group)
    {
        var modifier = group.IsSaturated
          ? "saturated"
          : group.IsRateLimited
            ? "blocked"
            : group.BlockedJobCount > 0
              ? "blocked"
              : GetOccupancyRatio(group) >= 0.8
                ? "high"
                : "nominal";

        return string.Concat("groups-state groups-state--", modifier);
    }

    private static double GetOccupancyRatio(ConcurrencyGroupInspectionSummary group)
      => group.EffectiveLimit <= 0 ? 0 : (double)group.CurrentOccupancy / group.EffectiveLimit;

    private static string LimitSourceText(ConcurrencyGroupInspectionSummary group)
      => group.OverrideLimit is not null
        ? "Override"
        : group.DefaultLimit is not null ? "Code default" : "Built-in default";

    private static string LimitSourceTitle(ConcurrencyGroupInspectionSummary group)
      => group.OverrideLimit is not null
        ? string.Create(CultureInfo.InvariantCulture, $"Override limit {DashboardFormat.Count(group.OverrideLimit.Value)}.")
        : group.DefaultLimit is not null
          ? string.Create(CultureInfo.InvariantCulture, $"Code default limit {DashboardFormat.Count(group.DefaultLimit.Value)}.")
          : "Built-in default limit 1.";

    private static string LimitSourceClass(ConcurrencyGroupInspectionSummary group)
      => group.OverrideLimit is not null
        ? "groups-source groups-source--override"
        : group.DefaultLimit is not null
          ? "groups-source groups-source--default"
          : "groups-source";

    private static string LimitInputLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Limit for concurrency group {group.GroupKey}");

    private static string EditLimitLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Edit limit for concurrency group {group.GroupKey}");

    private static string SaveLimitLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Save limit for concurrency group {group.GroupKey}");

    private static string CancelEditLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Cancel limit edit for concurrency group {group.GroupKey}");

    private static string ResetLimitLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Reset limit override for concurrency group {group.GroupKey}");

    private static string FormatRateLimit(ConcurrencyGroupRateLimit? rateLimit)
      => rateLimit is null
        ? "Unlimited"
        : string.Create(
          CultureInfo.InvariantCulture,
          $"{DashboardFormat.Count(rateLimit.PermitCount)} / {FormatRatePeriod(rateLimit.Period)}");

    private static string FormatRatePeriod(TimeSpan period)
    {
        if (period.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{period.TotalHours:0.###} h");
        }

        if (period.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{period.TotalMinutes:0.###} m");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{period.TotalSeconds:0.###} s");
    }

    private static string RateLimitSourceText(ConcurrencyGroupInspectionSummary group)
      => group.HasRateLimitOverride
        ? group.OverrideRateLimit is null ? "Override: unlimited" : "Override"
        : group.DefaultRateLimit is null ? "Built-in unlimited" : "Code default";

    private static string RateLimitSourceTitle(ConcurrencyGroupInspectionSummary group)
      => group.HasRateLimitOverride
        ? group.OverrideRateLimit is null
          ? "Live override disables the code-defined rate limit."
          : string.Create(CultureInfo.InvariantCulture, $"Live rate override {FormatRateLimit(group.OverrideRateLimit)}.")
        : group.DefaultRateLimit is null
          ? "No start-rate limit is configured."
          : string.Create(CultureInfo.InvariantCulture, $"Code default rate {FormatRateLimit(group.DefaultRateLimit)}.");

    private static string RateLimitSourceClass(ConcurrencyGroupInspectionSummary group)
      => group.HasRateLimitOverride
        ? "groups-source groups-source--override"
        : group.DefaultRateLimit is not null
          ? "groups-source groups-source--default"
          : "groups-source";

    private bool TryCreateRateLimit(
        [NotNullWhen(true)] out ConcurrencyGroupRateLimit? rateLimit,
        [NotNullWhen(false)] out string? validationMessage)
    {
        if (this._ratePermitInput <= 0)
        {
            rateLimit = null;
            validationMessage = "Permit count must be positive.";
            return false;
        }

        if (this._ratePeriodInput <= 0)
        {
            rateLimit = null;
            validationMessage = "Period must be positive.";
            return false;
        }

        try
        {
            var ticksPerUnit = this._ratePeriodUnit switch
            {
                HoursUnit => TimeSpan.TicksPerHour,
                MinutesUnit => TimeSpan.TicksPerMinute,
                _ => TimeSpan.TicksPerSecond,
            };
            var periodTicks = decimal.ToInt64(this._ratePeriodInput * ticksPerUnit);
            rateLimit = new ConcurrencyGroupRateLimit(this._ratePermitInput, TimeSpan.FromTicks(periodTicks));
            validationMessage = null;
            return true;
        }
        catch (OverflowException)
        {
            rateLimit = null;
            validationMessage = "Period is outside the supported range.";
            return false;
        }
    }

    private static (decimal Value, string Unit) ToRatePeriodInput(TimeSpan period)
    {
        if (period.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return ((decimal)period.Ticks / TimeSpan.TicksPerHour, HoursUnit);
        }

        if (period.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return ((decimal)period.Ticks / TimeSpan.TicksPerMinute, MinutesUnit);
        }

        return ((decimal)period.Ticks / TimeSpan.TicksPerSecond, SecondsUnit);
    }

    private static string RatePermitInputLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Rate permit count for concurrency group {group.GroupKey}");

    private static string RatePeriodInputLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Rate period for concurrency group {group.GroupKey}");

    private static string RatePeriodUnitInputLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Rate period unit for concurrency group {group.GroupKey}");

    private static string EditRateLimitLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Edit rate limit for concurrency group {group.GroupKey}");

    private static string SaveRateLimitLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Save rate limit for concurrency group {group.GroupKey}");

    private static string CancelRateEditLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Cancel rate-limit edit for concurrency group {group.GroupKey}");

    private static string SetUnlimitedRateLimitLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Set unlimited rate override for concurrency group {group.GroupKey}");

    private static string ResetRateLimitLabel(ConcurrencyGroupInspectionSummary group)
      => string.Create(CultureInfo.InvariantCulture, $"Reset rate-limit override for concurrency group {group.GroupKey}");
}
