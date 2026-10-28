namespace Sheddueller.Dashboard.Internal;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

/// <summary>
/// Units a duration can be entered in on the settings page.
/// </summary>
internal enum DashboardDurationUnit
{
    Seconds,
    Minutes,
    Hours,
    Days,
}

/// <summary>
/// A duration as typed on the settings page: the amount text and its unit. The amount may have a fractional part,
/// so a stored duration always round-trips exactly instead of being rounded to whole hours or minutes.
/// </summary>
internal sealed record DashboardDurationInput(
    string Amount,
    DashboardDurationUnit Unit)
{
    /// <summary>
    /// The shortest duration accepted.
    /// </summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest duration accepted (about 100 years), which keeps cleanup cutoffs inside the date range.
    /// </summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(36_500);

    private const NumberStyles AmountStyles = NumberStyles.AllowLeadingWhite
      | NumberStyles.AllowTrailingWhite
      | NumberStyles.AllowLeadingSign
      | NumberStyles.AllowDecimalPoint;

    private static readonly DashboardDurationUnit[] LargestFirst =
    [
        DashboardDurationUnit.Days,
        DashboardDurationUnit.Hours,
        DashboardDurationUnit.Minutes,
        DashboardDurationUnit.Seconds,
    ];

    /// <summary>
    /// Units offered in the unit picker, smallest first.
    /// </summary>
    public static IReadOnlyList<DashboardDurationUnit> Units { get; } =
    [
        DashboardDurationUnit.Seconds,
        DashboardDurationUnit.Minutes,
        DashboardDurationUnit.Hours,
        DashboardDurationUnit.Days,
    ];

    /// <summary>
    /// Whether the amount reads as exactly one, so the unit is shown in the singular.
    /// </summary>
    public bool IsSingular
      => decimal.TryParse(this.Amount, AmountStyles, CultureInfo.InvariantCulture, out var amount) && amount == 1;

    /// <summary>
    /// Shows <paramref name="value"/> in the largest unit that holds it as a whole number, falling back to exact
    /// fractional seconds.
    /// </summary>
    public static DashboardDurationInput FromTimeSpan(TimeSpan value)
    {
        foreach (var unit in LargestFirst)
        {
            var unitTicks = UnitTicks(unit);
            if (value.Ticks % unitTicks == 0)
            {
                return new DashboardDurationInput(FormatAmount((decimal)value.Ticks / unitTicks), unit);
            }
        }

        return new DashboardDurationInput(
          FormatAmount((decimal)value.Ticks / TimeSpan.TicksPerSecond),
          DashboardDurationUnit.Seconds);
    }

    /// <summary>
    /// The unit's name for the unit picker.
    /// </summary>
    public static string UnitLabel(
        DashboardDurationUnit unit,
        bool singular)
      => unit switch
      {
          DashboardDurationUnit.Seconds => singular ? "second" : "seconds",
          DashboardDurationUnit.Minutes => singular ? "minute" : "minutes",
          DashboardDurationUnit.Hours => singular ? "hour" : "hours",
          DashboardDurationUnit.Days => singular ? "day" : "days",
          _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown duration unit."),
      };

    /// <summary>
    /// Reads the duration, or explains why it can't be used.
    /// </summary>
    public bool TryGetValue(
        out TimeSpan value,
        [NotNullWhen(false)] out string? error)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(this.Amount))
        {
            error = "Enter a duration.";
            return false;
        }

        if (!decimal.TryParse(this.Amount, AmountStyles, CultureInfo.InvariantCulture, out var amount))
        {
            error = "Enter a number, such as 7 or 1.5.";
            return false;
        }

        if (amount <= 0)
        {
            error = "Must be more than zero.";
            return false;
        }

        var unitTicks = UnitTicks(this.Unit);
        if (amount > (decimal)Maximum.Ticks / unitTicks)
        {
            error = "Must be 36,500 days or less.";
            return false;
        }

        var ticks = (long)decimal.Round(amount * unitTicks, MidpointRounding.AwayFromZero);
        if (ticks < Minimum.Ticks)
        {
            error = "Must be at least 1 second.";
            return false;
        }

        value = TimeSpan.FromTicks(ticks);
        error = null;
        return true;
    }

    private static long UnitTicks(DashboardDurationUnit unit)
      => unit switch
      {
          DashboardDurationUnit.Seconds => TimeSpan.TicksPerSecond,
          DashboardDurationUnit.Minutes => TimeSpan.TicksPerMinute,
          DashboardDurationUnit.Hours => TimeSpan.TicksPerHour,
          DashboardDurationUnit.Days => TimeSpan.TicksPerDay,
          _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown duration unit."),
      };

    private static string FormatAmount(decimal amount)
      => amount.ToString("0.############################", CultureInfo.InvariantCulture);
}
