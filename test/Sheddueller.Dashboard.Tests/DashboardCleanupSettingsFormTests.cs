namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardCleanupSettingsFormTests
{
    private static readonly ShedduellerCleanupConfiguration DefaultConfiguration = new(
      new JobRetentionCleanupConfiguration(
        Enabled: true,
        CompletedRetention: TimeSpan.FromDays(1),
        FailedRetention: TimeSpan.FromDays(7),
        CanceledRetention: null,
        CleanupInterval: TimeSpan.FromHours(1),
        BatchSize: 1000),
      new JobEventCleanupConfiguration(TimeSpan.FromDays(7), TimeSpan.FromHours(1)),
      new MetricsCleanupConfiguration(TimeSpan.FromDays(7), TimeSpan.FromHours(1)));

    [Fact]
    public void Form_GroupCleanupChanges_RoundTripsAndTracksChanges()
    {
        var baseline = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);
        form.GroupCleanupEnabled = false;
        form.GroupBatchSize = "25";
        form.SetDuration(DashboardCleanupSetting.GroupIdleRetention, new("2", DashboardDurationUnit.Days));
        form.SetDuration(DashboardCleanupSetting.GroupCleanupInterval, new("5", DashboardDurationUnit.Minutes));
        form.HasChangesFrom(baseline).ShouldBeTrue();
        form.TryCreateConfiguration(out var config).ShouldBeTrue();
        config.ConcurrencyGroups.ShouldBe(new(false, TimeSpan.FromDays(2), TimeSpan.FromMinutes(5), 25));
    }

    [Theory]
    [InlineData(7 * 24 * 60 * 60, "7", "Days")]
    [InlineData(36 * 60 * 60, "36", "Hours")]
    [InlineData(90 * 60, "90", "Minutes")]
    [InlineData(45, "45", "Seconds")]
    public void Duration_WholeUnit_UsesLargestUnitThatFits(
        int seconds,
        string amount,
        string unit)
    {
        var duration = DashboardDurationInput.FromTimeSpan(TimeSpan.FromSeconds(seconds));

        duration.ShouldBe(new DashboardDurationInput(amount, Enum.Parse<DashboardDurationUnit>(unit)));
    }

    [Fact]
    public void Duration_FractionalSeconds_ShowsExactAmount()
    {
        var duration = DashboardDurationInput.FromTimeSpan(TimeSpan.FromMilliseconds(1500));

        duration.ShouldBe(new DashboardDurationInput("1.5", DashboardDurationUnit.Seconds));
        duration.TryGetValue(out var value, out _).ShouldBeTrue();
        value.ShouldBe(TimeSpan.FromMilliseconds(1500));
    }

    [Theory]
    [InlineData("1.5", "Hours", 90 * 60)]
    [InlineData(" 2 ", "Days", 2 * 24 * 60 * 60)]
    [InlineData("0.25", "Minutes", 15)]
    public void Duration_DecimalAmount_ReadsExactValue(
        string amount,
        string unit,
        int expectedSeconds)
    {
        new DashboardDurationInput(amount, Enum.Parse<DashboardDurationUnit>(unit)).TryGetValue(out var value, out var error).ShouldBeTrue();

        value.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "Hours", "Enter a duration.")]
    [InlineData("seven", "Hours", "Enter a number, such as 7 or 1.5.")]
    [InlineData("1,5", "Hours", "Enter a number, such as 7 or 1.5.")]
    [InlineData("0", "Hours", "Must be more than zero.")]
    [InlineData("-3", "Days", "Must be more than zero.")]
    [InlineData("0.5", "Seconds", "Must be at least 1 second.")]
    [InlineData("36501", "Days", "Must be 36,500 days or less.")]
    public void Duration_InvalidAmount_ExplainsProblem(
        string amount,
        string unit,
        string expectedError)
    {
        new DashboardDurationInput(amount, Enum.Parse<DashboardDurationUnit>(unit)).TryGetValue(out _, out var error).ShouldBeFalse();

        error.ShouldBe(expectedError);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("2", false)]
    [InlineData("", false)]
    public void Duration_AmountOfOne_ShowsSingularUnit(
        string amount,
        bool expectedSingular)
      => new DashboardDurationInput(amount, DashboardDurationUnit.Days).IsSingular.ShouldBe(expectedSingular);

    [Fact]
    public void Form_LoadedConfiguration_SavesWithoutRounding()
    {
        var configuration = new ShedduellerCleanupConfiguration(
          new JobRetentionCleanupConfiguration(
            Enabled: false,
            CompletedRetention: TimeSpan.FromMinutes(90),
            FailedRetention: null,
            CanceledRetention: TimeSpan.FromHours(36),
            CleanupInterval: TimeSpan.FromSeconds(45),
            BatchSize: 250),
          new JobEventCleanupConfiguration(TimeSpan.FromMilliseconds(90_500), TimeSpan.FromMinutes(5)),
          new MetricsCleanupConfiguration(TimeSpan.FromDays(30), TimeSpan.FromMinutes(15)));

        var form = DashboardCleanupSettingsForm.FromConfiguration(configuration);

        form.TryCreateConfiguration(out var saved).ShouldBeTrue();
        saved.ShouldBe(configuration);
    }

    [Fact]
    public void Form_Unchanged_HasNoChanges()
    {
        var baseline = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.HasChangesFrom(baseline).ShouldBeFalse();
        form.InvalidSettings.ShouldBeEmpty();
    }

    [Fact]
    public void Form_SameDurationInOtherUnit_HasNoChanges()
    {
        var baseline = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.SetDuration(DashboardCleanupSetting.FailedRetention, new DashboardDurationInput("168", DashboardDurationUnit.Hours));
        form.BatchSize = "1,000";

        form.HasChangesFrom(baseline).ShouldBeFalse();
    }

    [Fact]
    public void Form_ChangedDuration_HasChanges()
    {
        var baseline = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.SetDuration(DashboardCleanupSetting.MetricsRetention, new DashboardDurationInput("14", DashboardDurationUnit.Days));

        form.HasChangesFrom(baseline).ShouldBeTrue();
        form.TryCreateConfiguration(out var configuration).ShouldBeTrue();
        configuration.Metrics.Retention.ShouldBe(TimeSpan.FromDays(14));
    }

    [Fact]
    public void Form_ToggledSwitch_HasChanges()
    {
        var baseline = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.JobRetentionEnabled = false;

        form.HasChangesFrom(baseline).ShouldBeTrue();
    }

    [Fact]
    public void Form_InvalidValue_HasChangesAndCannotSave()
    {
        var baseline = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.SetDuration(DashboardCleanupSetting.EventCleanupInterval, new DashboardDurationInput(string.Empty, DashboardDurationUnit.Hours));

        form.HasChangesFrom(baseline).ShouldBeTrue();
        form.Error(DashboardCleanupSetting.EventCleanupInterval).ShouldBe("Enter a duration.");
        form.InvalidSettings.ShouldBe([DashboardCleanupSetting.EventCleanupInterval]);
        form.TryCreateConfiguration(out var configuration).ShouldBeFalse();
        configuration.ShouldBeNull();
    }

    [Fact]
    public void Form_KeepForeverTickedThenUnticked_HasNoChanges()
    {
        var baseline = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.SetKeptForever(DashboardCleanupSetting.CompletedRetention, keepForever: true);
        form.HasChangesFrom(baseline).ShouldBeTrue();

        form.SetKeptForever(DashboardCleanupSetting.CompletedRetention, keepForever: false);
        form.HasChangesFrom(baseline).ShouldBeFalse();
    }

    [Fact]
    public void Form_KeptForever_IgnoresHiddenDuration()
    {
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.IsKeptForever(DashboardCleanupSetting.CanceledRetention).ShouldBeTrue();
        form.Duration(DashboardCleanupSetting.CanceledRetention).ShouldBe(new DashboardDurationInput("7", DashboardDurationUnit.Days));

        form.SetDuration(DashboardCleanupSetting.CanceledRetention, new DashboardDurationInput("not a number", DashboardDurationUnit.Days));

        form.Error(DashboardCleanupSetting.CanceledRetention).ShouldBeNull();
        form.TryCreateConfiguration(out var configuration).ShouldBeTrue();
        configuration.JobRetention.CanceledRetention.ShouldBeNull();
    }

    [Fact]
    public void Form_KeepForever_OnlyAppliesToJobRetentions()
        => Should.Throw<ArgumentOutOfRangeException>(
          () => DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration)
            .SetKeptForever(DashboardCleanupSetting.MetricsRetention, keepForever: true));

    [Theory]
    [InlineData("", "Enter a batch size.")]
    [InlineData("12.5", "Enter a whole number, such as 1000.")]
    [InlineData("0", "Must be at least 1.")]
    [InlineData("3000000000", "Must be 2,147,483,647 or less.")]
    public void BatchSize_InvalidValue_ExplainsProblem(
        string text,
        string expectedError)
    {
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.BatchSize = text;

        form.Error(DashboardCleanupSetting.JobBatchSize).ShouldBe(expectedError);
    }

    [Fact]
    public void BatchSize_ThousandsSeparator_ReadsWholeNumber()
    {
        var form = DashboardCleanupSettingsForm.FromConfiguration(DefaultConfiguration);

        form.BatchSize = "2,500";

        form.TryCreateConfiguration(out var configuration).ShouldBeTrue();
        configuration.JobRetention.BatchSize.ShouldBe(2500);
    }
}
