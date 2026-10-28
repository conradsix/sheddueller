namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.ConcurrencyGroups;

using Shouldly;

public sealed class DashboardConcurrencyGroupFormatTests
{
    [Theory]
    [InlineData(10, 1_000, "10 per second")]
    [InlineData(1, 3_600_000, "1 per hour")]
    [InlineData(100, 900_000, "100 per 15 min")]
    [InlineData(5, 30_000, "5 per 30 s")]
    [InlineData(1_000, 7_200_000, "1,000 per 2 h")]
    [InlineData(3, 1_500, "3 per 1.5 s")]
    [InlineData(2, 500, "2 per 500 ms")]
    public void RateText_Period_UsesLargestWholeUnit(int permits, long periodMilliseconds, string expected)
      => DashboardConcurrencyGroupFormat.Rate(new ConcurrencyGroupRateLimit(permits, TimeSpan.FromMilliseconds(periodMilliseconds)))
        .ShouldBe(expected);

    [Fact]
    public void RateText_NoRateLimit_IsUnlimited()
      => DashboardConcurrencyGroupFormat.Rate(null).ShouldBe("Unlimited");

    [Theory]
    [InlineData(1, 10, false, 0, "Nominal")]
    [InlineData(8, 10, false, 0, "High")]
    [InlineData(10, 10, true, 0, "Saturated")]
    [InlineData(10, 10, true, 3, "Blocked")]
    [InlineData(2, 10, false, 1, "Blocked")]
    public void GroupState_OccupancyAndWaitingJobs_ReportsTheMostSevereCondition(
        int running,
        int limit,
        bool saturated,
        int blocked,
        string expected)
      => DashboardConcurrencyGroupFormat.State(new ConcurrencyGroupInspectionSummary("billing", limit, running, blocked, saturated, UpdatedAtUtc: null))
        .ShouldBe(Enum.Parse<DashboardConcurrencyGroupState>(expected));

    [Fact]
    public void LimitSource_Override_NamesTheDefaultItReplaces()
    {
        var group = Group() with { OverrideLimit = 5, DefaultLimit = 3 };

        DashboardConcurrencyGroupFormat.LimitSource(group).ShouldBe("Override");
        DashboardConcurrencyGroupFormat.LimitSourceTitle(group).ShouldBe("Set from the dashboard. Without it the limit is the code default of 3.");
    }

    [Fact]
    public void LimitSource_NothingConfigured_IsTheBuiltInDefault()
    {
        var group = Group();

        DashboardConcurrencyGroupFormat.LimitSource(group).ShouldBe("Built-in default");
        DashboardConcurrencyGroupFormat.DefaultLimitText(group).ShouldBe("the built-in default of 1");
    }

    [Fact]
    public void RateSource_UnlimitedOverrideOfCodeDefault_NamesTheCodeDefault()
    {
        var group = Group() with
        {
            DefaultRateLimit = new ConcurrencyGroupRateLimit(10, TimeSpan.FromSeconds(1)),
            HasRateLimitOverride = true,
        };

        DashboardConcurrencyGroupFormat.RateSource(group).ShouldBe("Override");
        DashboardConcurrencyGroupFormat.DefaultRateText(group).ShouldBe("the code default of 10 per second");
    }

    [Fact]
    public void BlockedTitle_WaitingJobs_SaysWhatTheyWaitFor()
    {
        var group = new ConcurrencyGroupInspectionSummary("billing", 2, 2, 3, IsSaturated: true, UpdatedAtUtc: null)
        {
            ConcurrencyBlockedJobCount = 2,
            RateBlockedJobCount = 1,
        };

        DashboardConcurrencyGroupFormat.BlockedTitle(group).ShouldBe("3 due jobs are waiting: 2 for a free slot, 1 for a rate permit.");
    }

    private static ConcurrencyGroupInspectionSummary Group()
      => new("billing", EffectiveLimit: 1, CurrentOccupancy: 0, BlockedJobCount: 0, IsSaturated: false, UpdatedAtUtc: null);
}
