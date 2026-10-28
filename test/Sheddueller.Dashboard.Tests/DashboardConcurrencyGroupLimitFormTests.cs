namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.ConcurrencyGroups;

using Shouldly;

public sealed class DashboardConcurrencyGroupLimitFormTests
{
    private static readonly ConcurrencyGroupRateLimit TenPerSecond = new(10, TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 5 ", 5)]
    [InlineData("12.0", 12)]
    [InlineData("2147483647", int.MaxValue)]
    public void LimitField_WholeNumberOfAtLeastOne_IsAccepted(string text, int expected)
    {
        DashboardConcurrencyGroupLimitForm.TryParseLimit(text, out var limit, out var error).ShouldBeTrue();

        limit.ShouldBe(expected);
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "Enter a limit.")]
    [InlineData("   ", "Enter a limit.")]
    [InlineData("0", "The limit must be at least 1.")]
    [InlineData("-3", "The limit must be at least 1.")]
    [InlineData("2.5", "Enter a whole number.")]
    [InlineData("five", "Enter a whole number.")]
    [InlineData("2147483648", "Enter a number no larger than 2,147,483,647.")]
    public void LimitField_InvalidText_ExplainsTheProblem(string text, string expectedError)
    {
        DashboardConcurrencyGroupLimitForm.TryParseLimit(text, out _, out var error).ShouldBeFalse();

        error.ShouldBe(expectedError);
    }

    [Theory]
    [InlineData("1", "Seconds", 1_000)]
    [InlineData("0.5", "Seconds", 500)]
    [InlineData("15", "Minutes", 900_000)]
    [InlineData("1.5", "Hours", 5_400_000)]
    public void RatePeriod_ValueAndUnit_ConvertsToTimeSpan(string text, string unit, long expectedMilliseconds)
    {
        DashboardConcurrencyGroupLimitForm.TryParsePeriod(text, Enum.Parse<DashboardRatePeriodUnit>(unit), out var period, out var error).ShouldBeTrue();

        period.ShouldBe(TimeSpan.FromMilliseconds(expectedMilliseconds));
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "Seconds", "Enter a period.")]
    [InlineData("soon", "Seconds", "Enter a number, such as 1 or 0.5.")]
    [InlineData("0", "Minutes", "The period must be longer than 0.")]
    [InlineData("-1", "Seconds", "The period must be longer than 0.")]
    [InlineData("0.00000000001", "Seconds", "The period is too short.")]
    [InlineData("99999999999", "Hours", "The period is too long.")]
    public void RatePeriod_InvalidText_ExplainsTheProblem(string text, string unit, string expectedError)
    {
        DashboardConcurrencyGroupLimitForm.TryParsePeriod(text, Enum.Parse<DashboardRatePeriodUnit>(unit), out _, out var error).ShouldBeFalse();

        error.ShouldBe(expectedError);
    }

    [Fact]
    public void RateFields_MoreThanOneStartPerMicrosecond_FlagsThePermitCount()
    {
        DashboardConcurrencyGroupLimitForm.TryParseRate(
          "2000",
          "0.001",
          DashboardRatePeriodUnit.Seconds,
          out var rate,
          out var permitsError,
          out var periodError).ShouldBeFalse();

        rate.ShouldBeNull();
        permitsError.ShouldNotBeNull();
        periodError.ShouldBeNull();
    }

    [Fact]
    public void RateFields_BothInvalid_ReportsEachFieldSeparately()
    {
        DashboardConcurrencyGroupLimitForm.TryParseRate(
          "0",
          "",
          DashboardRatePeriodUnit.Seconds,
          out _,
          out var permitsError,
          out var periodError).ShouldBeFalse();

        permitsError.ShouldBe("Allow at least 1 start.");
        periodError.ShouldBe("Enter a period.");
    }

    [Theory]
    [InlineData(3_600_000, "1", "Hours")]
    [InlineData(5_400_000, "90", "Minutes")]
    [InlineData(1_500, "1.5", "Seconds")]
    [InlineData(500, "0.5", "Seconds")]
    public void PeriodInput_StoredPeriod_UsesLargestWholeUnit(long milliseconds, string expectedValue, string expectedUnit)
    {
        var (value, unit) = DashboardConcurrencyGroupLimitForm.ToPeriodInput(TimeSpan.FromMilliseconds(milliseconds));

        value.ShouldBe(expectedValue);
        unit.ShouldBe(Enum.Parse<DashboardRatePeriodUnit>(expectedUnit));
    }

    [Fact]
    public void EditForm_Opened_StartsFromTheEffectiveValues()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group(limit: 4) with
        {
            EffectiveRateLimit = new ConcurrencyGroupRateLimit(100, TimeSpan.FromMinutes(15)),
        });

        form.Limit.ShouldBe("4");
        form.RateUnlimited.ShouldBeFalse();
        form.RatePermits.ShouldBe("100");
        form.RatePeriod.ShouldBe("15");
        form.RatePeriodUnit.ShouldBe(DashboardRatePeriodUnit.Minutes);
    }

    [Fact]
    public void EditForm_UnlimitedOverrideOfCodeDefault_OffersTheCodeDefaultAsTheRate()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group() with
        {
            DefaultRateLimit = new ConcurrencyGroupRateLimit(5, TimeSpan.FromMinutes(1)),
            HasRateLimitOverride = true,
        });

        form.RateUnlimited.ShouldBeTrue();
        form.RatePermits.ShouldBe("5");
        form.RatePeriod.ShouldBe("1");
        form.RatePeriodUnit.ShouldBe(DashboardRatePeriodUnit.Minutes);
    }

    [Fact]
    public void EditForm_Unchanged_HasNothingToSave()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group(limit: 3) with { EffectiveRateLimit = TenPerSecond });

        form.TryGetChanges(out var changes).ShouldBeTrue();

        changes.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void EditForm_NewLimitOnly_ChangesOnlyTheLimit()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group(limit: 3) with { EffectiveRateLimit = TenPerSecond });
        form.Limit = "8";

        form.TryGetChanges(out var changes).ShouldBeTrue();

        changes.ShouldBe(new DashboardConcurrencyGroupLimitChanges(8, RateChanged: false, TenPerSecond));
    }

    [Fact]
    public void EditForm_SameRateInAnotherUnit_IsNotAChange()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group() with
        {
            EffectiveRateLimit = new ConcurrencyGroupRateLimit(10, TimeSpan.FromMinutes(1)),
        });
        form.RatePeriod = "60";
        form.RatePeriodUnit = DashboardRatePeriodUnit.Seconds;

        form.TryGetChanges(out var changes).ShouldBeTrue();

        changes.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void EditForm_SwitchedToUnlimited_RemovesTheRateLimit()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group() with { EffectiveRateLimit = TenPerSecond });
        form.RateUnlimited = true;

        form.TryGetChanges(out var changes).ShouldBeTrue();

        changes.Limit.ShouldBeNull();
        changes.RateChanged.ShouldBeTrue();
        changes.Rate.ShouldBeNull();
    }

    [Fact]
    public void EditForm_RateAddedToUnlimitedGroup_SetsTheRate()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group());
        form.RateUnlimited = false;
        form.RatePermits = "2";
        form.RatePeriod = "30";

        form.TryGetChanges(out var changes).ShouldBeTrue();

        changes.RateChanged.ShouldBeTrue();
        changes.Rate.ShouldBe(new ConcurrencyGroupRateLimit(2, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void EditForm_Unlimited_IgnoresTheHiddenRateFields()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group());
        form.RatePermits = string.Empty;

        form.TryGetChanges(out var changes).ShouldBeTrue();

        changes.IsEmpty.ShouldBeTrue();
        form.RatePermitsError.ShouldBeNull();
    }

    [Fact]
    public void EditForm_InvalidFields_SetsFieldErrorsAndSavesNothing()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group() with { EffectiveRateLimit = TenPerSecond });
        form.Limit = "0";
        form.RatePermits = string.Empty;

        form.TryGetChanges(out var changes).ShouldBeFalse();

        changes.ShouldBeNull();
        form.LimitError.ShouldBe("The limit must be at least 1.");
        form.RatePermitsError.ShouldBe("Enter how many jobs may start.");
        form.RatePeriodError.ShouldBeNull();
    }

    [Fact]
    public void EditForm_FieldEdited_ClearsThatFieldsError()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group() with { EffectiveRateLimit = TenPerSecond });
        form.Limit = string.Empty;
        form.RatePeriod = string.Empty;
        form.TryGetChanges(out _).ShouldBeFalse();

        form.Limit = "2";

        form.LimitError.ShouldBeNull();
        form.RatePeriodError.ShouldNotBeNull();
    }

    [Fact]
    public void EditForm_LimitSavedBeforeRateFailed_IsNotAppliedAgain()
    {
        var form = DashboardConcurrencyGroupLimitForm.For(Group(limit: 3));
        form.Limit = "6";
        form.RateUnlimited = false;
        form.TryGetChanges(out var first).ShouldBeTrue();
        first.Limit.ShouldBe(6);

        form.MarkLimitSaved(6);

        form.TryGetChanges(out var retry).ShouldBeTrue();
        retry.Limit.ShouldBeNull();
        retry.RateChanged.ShouldBeTrue();
    }

    private static ConcurrencyGroupInspectionSummary Group(int limit = 1)
      => new("billing", limit, CurrentOccupancy: 0, BlockedJobCount: 0, IsSaturated: false, UpdatedAtUtc: null);
}
