namespace Sheddueller.Tests;

using System.Globalization;

using Sheddueller.Scheduling;

using Shouldly;

public sealed class CronScheduleTests
{
    [Theory]
    [InlineData("* * * * *", "2026-04-19T10:30:00Z", "2026-04-19T10:31:00Z")]
    [InlineData("* * * * *", "2026-04-19T10:30:45Z", "2026-04-19T10:31:00Z")]
    [InlineData("*/25 * * * *", "2026-04-19T10:30:00Z", "2026-04-19T10:50:00Z")]
    [InlineData("*/25 * * * *", "2026-04-19T10:51:00Z", "2026-04-19T11:00:00Z")]
    [InlineData("10-40/15 * * * *", "2026-04-19T10:40:00Z", "2026-04-19T11:10:00Z")]
    [InlineData("15 */6 * * *", "2026-04-19T06:15:00Z", "2026-04-19T12:15:00Z")]
    [InlineData("3,13,23,33,43,53 * * * *", "2026-04-19T10:53:00Z", "2026-04-19T11:03:00Z")]
    [InlineData("10 0,6,12,18 * * *", "2026-04-19T18:10:00Z", "2026-04-20T00:10:00Z")]
    [InlineData("0 3 1 * *", "2026-04-01T03:00:00Z", "2026-05-01T03:00:00Z")]
    [InlineData("40 13 * * 0", "2026-04-19T13:40:00Z", "2026-04-26T13:40:00Z")]
    [InlineData("0 14,15 * * 2", "2026-04-19T10:30:00Z", "2026-04-21T14:00:00Z")]
    [InlineData("0 0 * * 7", "2026-04-19T10:30:00Z", "2026-04-26T00:00:00Z")]
    [InlineData("0 0 1 1 *", "2026-12-31T23:59:00Z", "2027-01-01T00:00:00Z")]
    [InlineData("0 0 31 * *", "2026-03-31T00:00:00Z", "2026-05-31T00:00:00Z")]
    [InlineData("0 0 29 2 *", "2026-03-01T00:00:00Z", "2028-02-29T00:00:00Z")]
    [InlineData("0 0 29 2 *", "2096-03-01T00:00:00Z", "2104-02-29T00:00:00Z")]
    [InlineData("0 9 * JAN-mar mon", "2026-04-19T10:30:00Z", "2027-01-04T09:00:00Z")]
    [InlineData(" 0\t9  * * * ", "2026-04-19T10:30:00Z", "2026-04-20T09:00:00Z")]
    public void NextOccurrence_Expression_ReturnsExpectedInstant(string expression, string after, string expected)
      => CronSchedule.GetNextOccurrenceAfter(expression, Instant(after)).ShouldBe(Instant(expected));

    [Theory]
    [InlineData("2026-04-19T10:30:00Z", "2026-04-24T00:00:00Z")]
    [InlineData("2026-05-09T00:00:00Z", "2026-05-13T00:00:00Z")]
    public void NextOccurrence_BothDayFieldsRestricted_MatchesEither(string after, string expected)
      => CronSchedule.GetNextOccurrenceAfter("0 0 13 * 5", Instant(after)).ShouldBe(Instant(expected));

    [Fact]
    public void NextOccurrence_DayFieldStarred_RequiresBoth()
      => CronSchedule.GetNextOccurrenceAfter("0 0 */2 * 1", Instant("2026-04-19T10:30:00Z"))
        .ShouldBe(Instant("2026-04-27T00:00:00Z"));

    [Theory]
    [InlineData("not cron")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("-1 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("5/15 * * * *")]
    [InlineData("1,,2 * * * *")]
    [InlineData("@daily")]
    [InlineData("0 0 L * *")]
    [InlineData("0 0 15W * *")]
    [InlineData("0 0 * * 5L")]
    [InlineData("0 0 * * 6#3")]
    [InlineData("0 0 ? * *")]
    [InlineData("H * * * *")]
    [InlineData("* * * JANUARY *")]
    [InlineData("* * * * JAN")]
    [InlineData("0 0 30 2 *")]
    public void Validate_UnsupportedOrMalformedExpression_ThrowsFormatException(string expression)
      => Should.Throw<FormatException>(() => CronSchedule.Validate(expression));

    private static DateTimeOffset Instant(string text)
      => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
}
