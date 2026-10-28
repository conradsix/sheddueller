namespace Sheddueller.Scheduling;

using System.Globalization;

/// <summary>
/// Parses standard five-field cron expressions: <c>*</c>, lists, ranges, steps, and three-letter month and day names.
/// </summary>
internal static class CronParser
{
    private static readonly char[] FieldSeparators = [' ', '\t'];

    private static readonly CronField Minute = new("minute", 0, 59, Names: null);
    private static readonly CronField Hour = new("hour", 0, 23, Names: null);
    private static readonly CronField DayOfMonth = new("day-of-month", 1, 31, Names: null);
    private static readonly CronField Month = new("month", 1, 12, ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]);
    private static readonly CronField DayOfWeek = new("day-of-week", 0, 7, ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"]);

    public static CronFields Parse(string expression)
    {
        var fields = expression.Split(FieldSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            throw new FormatException(
              $"Cron expression '{expression}' must have five fields: minute, hour, day-of-month, month and day-of-week.");
        }

        return new CronFields(
          ParseField(expression, fields[0], Minute),
          ParseField(expression, fields[1], Hour),
          ParseField(expression, fields[2], DayOfMonth),
          ParseField(expression, fields[3], Month),
          FoldSundays(ParseField(expression, fields[4], DayOfWeek)),
          DayOfMonthStarred: fields[2].StartsWith('*'),
          DayOfWeekStarred: fields[4].StartsWith('*'));
    }

    private static ulong ParseField(string expression, string text, CronField field)
    {
        var mask = 0UL;
        foreach (var item in text.Split(','))
        {
            mask |= ParseItem(expression, text, item, field);
        }

        return mask;
    }

    private static ulong ParseItem(string expression, string text, string item, CronField field)
    {
        var slash = item.IndexOf('/', StringComparison.Ordinal);
        var range = slash < 0 ? item : item[..slash];
        var step = 1;
        if (slash >= 0 && !TryParseNumber(item[(slash + 1)..], 1, field.Max, out step))
        {
            throw Invalid(expression, text, field);
        }

        int low;
        int high;
        var dash = range.IndexOf('-', StringComparison.Ordinal);
        if (range == "*")
        {
            (low, high) = (field.Min, field.Max);
        }
        else if (dash >= 0)
        {
            if (!TryParseValue(range[..dash], field, out low) || !TryParseValue(range[(dash + 1)..], field, out high) || low > high)
            {
                throw Invalid(expression, text, field);
            }
        }
        else
        {
            // A step needs a range to walk: '5/15' is a non-standard extension.
            if (slash >= 0 || !TryParseValue(range, field, out low))
            {
                throw Invalid(expression, text, field);
            }

            high = low;
        }

        var mask = 0UL;
        for (var value = low; value <= high; value += step)
        {
            mask |= 1UL << value;
        }

        return mask;
    }

    private static bool TryParseValue(string text, CronField field, out int value)
    {
        if (TryParseNumber(text, field.Min, field.Max, out value))
        {
            return true;
        }

        var nameIndex = field.Names is null
          ? -1
          : Array.FindIndex(field.Names, name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));

        value = field.Min + nameIndex;
        return nameIndex >= 0;
    }

    private static bool TryParseNumber(string text, int min, int max, out int value)
      => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    // Day-of-week accepts 7 as a second spelling of Sunday (0).
    private static ulong FoldSundays(ulong daysOfWeek)
      => (daysOfWeek | (daysOfWeek >> 7)) & 0b111_1111;

    private static FormatException Invalid(string expression, string text, CronField field)
      => new($"Cron expression '{expression}' has an invalid {field.Name} field: '{text}'.");

    private sealed record CronField(string Name, int Min, int Max, string[]? Names);
}
