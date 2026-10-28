namespace Sheddueller.Dashboard.Internal;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Sheddueller.Inspection.ConcurrencyGroups;

/// <summary>
/// Unit of the period in the rate-limit form.
/// </summary>
internal enum DashboardRatePeriodUnit
{
    Seconds,
    Minutes,
    Hours,
}

/// <summary>
/// What saving the edit-limits form changes. A <see langword="null"/> <see cref="Limit"/> leaves the concurrency
/// limit alone; <see cref="Rate"/> applies only when <see cref="RateChanged"/>, and <see langword="null"/> there
/// means unlimited.
/// </summary>
internal sealed record DashboardConcurrencyGroupLimitChanges(
    int? Limit,
    bool RateChanged,
    ConcurrencyGroupRateLimit? Rate)
{
    public bool IsEmpty
      => this.Limit is null && !this.RateChanged;
}

/// <summary>
/// The edit-limits form for one concurrency group: the raw text of each field, field-level validation, and the
/// changes to apply compared with the values in effect when the form opened.
/// </summary>
internal sealed class DashboardConcurrencyGroupLimitForm
{
    private static readonly ConcurrencyGroupRateLimit FallbackRateInput = new(1, TimeSpan.FromSeconds(1));

    private string _limit;
    private bool _rateUnlimited;
    private string _ratePermits;
    private string _ratePeriod;
    private DashboardRatePeriodUnit _ratePeriodUnit;

    private DashboardConcurrencyGroupLimitForm(
        string groupKey,
        int savedLimit,
        ConcurrencyGroupRateLimit? savedRate,
        ConcurrencyGroupRateLimit rateInput)
    {
        this.GroupKey = groupKey;
        this.SavedLimit = savedLimit;
        this.SavedRate = savedRate;
        this._limit = savedLimit.ToString(CultureInfo.InvariantCulture);
        this._rateUnlimited = savedRate is null;
        this._ratePermits = rateInput.PermitCount.ToString(CultureInfo.InvariantCulture);
        (this._ratePeriod, this._ratePeriodUnit) = ToPeriodInput(rateInput.Period);
    }

    public string GroupKey { get; }

    /// <summary>
    /// The concurrency limit in effect (or last saved); the limit is applied only when the form differs from it.
    /// </summary>
    public int SavedLimit { get; private set; }

    /// <summary>
    /// The start rate in effect (or last saved), <see langword="null"/> for unlimited; the rate is applied only when
    /// the form differs from it.
    /// </summary>
    public ConcurrencyGroupRateLimit? SavedRate { get; private set; }

    public string Limit
    {
        get => this._limit;
        set
        {
            this._limit = value ?? string.Empty;
            this.LimitError = null;
        }
    }

    public bool RateUnlimited
    {
        get => this._rateUnlimited;
        set
        {
            this._rateUnlimited = value;
            this.RatePermitsError = null;
            this.RatePeriodError = null;
        }
    }

    public string RatePermits
    {
        get => this._ratePermits;
        set
        {
            this._ratePermits = value ?? string.Empty;
            this.RatePermitsError = null;
        }
    }

    public string RatePeriod
    {
        get => this._ratePeriod;
        set
        {
            this._ratePeriod = value ?? string.Empty;
            this.RatePeriodError = null;
            this.RatePermitsError = null;
        }
    }

    public DashboardRatePeriodUnit RatePeriodUnit
    {
        get => this._ratePeriodUnit;
        set
        {
            this._ratePeriodUnit = value;
            this.RatePeriodError = null;
            this.RatePermitsError = null;
        }
    }

    public string? LimitError { get; private set; }

    public string? RatePermitsError { get; private set; }

    public string? RatePeriodError { get; private set; }

    /// <summary>
    /// Opens the form on a group's effective limit and rate. When the rate is unlimited the rate fields start from
    /// the code default, or one start per second.
    /// </summary>
    public static DashboardConcurrencyGroupLimitForm For(ConcurrencyGroupInspectionSummary group)
      => new(
        group.GroupKey,
        group.EffectiveLimit,
        group.EffectiveRateLimit,
        group.EffectiveRateLimit ?? group.DefaultRateLimit ?? FallbackRateInput);

    /// <summary>
    /// Validates every field, setting the field errors, and returns what saving would change.
    /// </summary>
    public bool TryGetChanges([NotNullWhen(true)] out DashboardConcurrencyGroupLimitChanges? changes)
    {
        var limitValid = TryParseLimit(this._limit, out var limit, out var limitError);
        this.LimitError = limitError;

        ConcurrencyGroupRateLimit? rate = null;
        var rateValid = true;
        if (this._rateUnlimited)
        {
            this.RatePermitsError = null;
            this.RatePeriodError = null;
        }
        else
        {
            rateValid = TryParseRate(
              this._ratePermits,
              this._ratePeriod,
              this._ratePeriodUnit,
              out rate,
              out var permitsError,
              out var periodError);
            this.RatePermitsError = permitsError;
            this.RatePeriodError = periodError;
        }

        if (!limitValid || !rateValid)
        {
            changes = null;
            return false;
        }

        changes = new DashboardConcurrencyGroupLimitChanges(
          limit == this.SavedLimit ? null : limit,
          !Equals(rate, this.SavedRate),
          rate);
        return true;
    }

    /// <summary>
    /// Records a limit that was saved, so a retry after a later step fails does not apply it again.
    /// </summary>
    public void MarkLimitSaved(int limit)
      => this.SavedLimit = limit;

    /// <summary>
    /// Records a start rate that was saved; <see langword="null"/> for unlimited.
    /// </summary>
    public void MarkRateSaved(ConcurrencyGroupRateLimit? rate)
      => this.SavedRate = rate;

    public static bool TryParseLimit(
        string? text,
        out int limit,
        [NotNullWhen(false)] out string? error)
      => TryParseCount(text, "Enter a limit.", "The limit must be at least 1.", out limit, out error);

    /// <summary>
    /// Parses a start rate. Each field gets its own error; the combination must leave at least one microsecond
    /// between starts, as the scheduler requires.
    /// </summary>
    public static bool TryParseRate(
        string? permits,
        string? period,
        DashboardRatePeriodUnit unit,
        [NotNullWhen(true)] out ConcurrencyGroupRateLimit? rate,
        out string? permitsError,
        out string? periodError)
    {
        var permitsValid = TryParseCount(permits, "Enter how many jobs may start.", "Allow at least 1 start.", out var permitCount, out permitsError);
        var periodValid = TryParsePeriod(period, unit, out var parsedPeriod, out periodError);
        if (permitsValid && periodValid && parsedPeriod.TotalMicroseconds / permitCount < 1)
        {
            permitsError = "That is more than one start per microsecond. Allow fewer starts or use a longer period.";
            permitsValid = false;
        }

        rate = permitsValid && periodValid ? new ConcurrencyGroupRateLimit(permitCount, parsedPeriod) : null;
        return rate is not null;
    }

    public static bool TryParsePeriod(
        string? text,
        DashboardRatePeriodUnit unit,
        out TimeSpan period,
        [NotNullWhen(false)] out string? error)
    {
        period = TimeSpan.Zero;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "Enter a period.";
            return false;
        }

        if (!decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            error = "Enter a number, such as 1 or 0.5.";
            return false;
        }

        if (value <= 0)
        {
            error = "The period must be longer than 0.";
            return false;
        }

        var ticksPerUnit = TicksPerUnit(unit);
        if (value > (decimal)TimeSpan.MaxValue.Ticks / ticksPerUnit)
        {
            error = "The period is too long.";
            return false;
        }

        var ticks = decimal.Round(value * ticksPerUnit, MidpointRounding.AwayFromZero);
        if (ticks < 1)
        {
            error = "The period is too short.";
            return false;
        }

        period = TimeSpan.FromTicks(decimal.ToInt64(ticks));
        error = null;
        return true;
    }

    /// <summary>
    /// The period expressed in the largest whole unit, for the form's period and unit fields.
    /// </summary>
    public static (string Value, DashboardRatePeriodUnit Unit) ToPeriodInput(TimeSpan period)
    {
        if (period.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return ((period.Ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture), DashboardRatePeriodUnit.Hours);
        }

        if (period.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return ((period.Ticks / TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture), DashboardRatePeriodUnit.Minutes);
        }

        var seconds = (decimal)period.Ticks / TimeSpan.TicksPerSecond;
        return (seconds.ToString("0.#######", CultureInfo.InvariantCulture), DashboardRatePeriodUnit.Seconds);
    }

    private static long TicksPerUnit(DashboardRatePeriodUnit unit)
      => unit switch
      {
          DashboardRatePeriodUnit.Hours => TimeSpan.TicksPerHour,
          DashboardRatePeriodUnit.Minutes => TimeSpan.TicksPerMinute,
          _ => TimeSpan.TicksPerSecond,
      };

    private static bool TryParseCount(
        string? text,
        string emptyMessage,
        string minimumMessage,
        out int value,
        [NotNullWhen(false)] out string? error)
    {
        value = 0;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = emptyMessage;
            return false;
        }

        if (!decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || number != decimal.Truncate(number))
        {
            error = "Enter a whole number.";
            return false;
        }

        if (number < 1)
        {
            error = minimumMessage;
            return false;
        }

        if (number > int.MaxValue)
        {
            error = string.Concat("Enter a number no larger than ", DashboardFormat.Count(int.MaxValue), ".");
            return false;
        }

        value = (int)number;
        error = null;
        return true;
    }
}
