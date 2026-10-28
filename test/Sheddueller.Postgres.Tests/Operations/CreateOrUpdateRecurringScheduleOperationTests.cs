namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class CreateOrUpdateRecurringScheduleOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CreateOrUpdateRecurringSchedule_Create_PersistsDefinitionAndGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        var result = await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule(
          "schedule-a",
          priority: 5,
          groupKeys: ["beta", "alpha", "alpha"],
          tags: [new JobTag("tenant", "acme"), new JobTag("domain", "payments")],
          retryPolicy: new RetryPolicy(3, RetryBackoffKind.Exponential, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)),
          overlapMode: RecurringOverlapMode.Allow));

        result.ShouldBe(RecurringScheduleUpsertResult.Created);
        var schedule = await context.ReadScheduleAsync("schedule-a");
        schedule.CronExpression.ShouldBe("* * * * *");
        schedule.IsPaused.ShouldBeFalse();
        schedule.OverlapMode.ShouldBe("Allow");
        schedule.Priority.ShouldBe(5);
        schedule.ServiceType.ShouldBe(PostgresTestData.ServiceType);
        schedule.MethodName.ShouldBe(PostgresTestData.MethodName);
        schedule.InvocationTargetKind.ShouldBe("Instance");
        schedule.MethodParameterBindings.ShouldBeNull();
        schedule.RetryPolicyConfigured.ShouldBeTrue();
        schedule.MaxAttempts.ShouldBe(3);
        schedule.RetryBackoffKind.ShouldBe("Exponential");
        schedule.RetryBaseDelayMs.ShouldBe(2000);
        schedule.RetryMaxDelayMs.ShouldBe(8000);
        schedule.NextFireAtUtc.ShouldNotBeNull();
        (await context.ReadScheduleGroupKeysAsync("schedule-a")).ShouldBe(["alpha", "beta"]);
        (await context.ReadScheduleTagsAsync("schedule-a")).ShouldBe([new JobTag("tenant", "acme"), new JobTag("domain", "payments")]);
    }

    [Fact]
    public async Task CreateOrUpdateRecurringSchedule_InvokeMetadata_PersistsTargetKindAndParameterBindings()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule(
          "schedule-a",
          invocationTargetKind: JobInvocationTargetKind.Static,
          methodParameterBindings:
          [
              new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
          ]));

        var schedule = await context.ReadScheduleAsync("schedule-a");

        schedule.InvocationTargetKind.ShouldBe("Static");
        schedule.MethodParameterBindings.ShouldBe(["CancellationToken"]);
    }

    [Fact]
    public async Task CreateOrUpdateRecurringSchedule_Unchanged_ReturnsUnchanged()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var request = PostgresTestData.CreateSchedule("schedule-a");
        await context.Store.CreateOrUpdateRecurringScheduleAsync(request);

        (await context.Store.CreateOrUpdateRecurringScheduleAsync(request)).ShouldBe(RecurringScheduleUpsertResult.Unchanged);
    }

    [Fact]
    public async Task CreateOrUpdateRecurringSchedule_UpdateActiveSchedule_ReplacesDefinitionAndGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule(
          "schedule-a",
          priority: 1,
          groupKeys: ["old"],
          tags: [new JobTag("tenant", "old")]));

        var result = await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule(
          "schedule-a",
          priority: 9,
          groupKeys: ["new"],
          tags: [new JobTag("source", "api"), new JobTag("tenant", "new")]));

        result.ShouldBe(RecurringScheduleUpsertResult.Updated);
        (await context.ReadScheduleAsync("schedule-a")).Priority.ShouldBe(9);
        (await context.ReadScheduleGroupKeysAsync("schedule-a")).ShouldBe(["new"]);
        (await context.ReadScheduleTagsAsync("schedule-a")).ShouldBe([new JobTag("source", "api"), new JobTag("tenant", "new")]);
    }

    [Fact]
    public async Task CreateOrUpdateRecurringSchedule_UpdatePausedSchedule_DefaultUpdateOptionsResumesSchedule()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", priority: 1));
        await context.Store.PauseRecurringScheduleAsync("schedule-a", DateTimeOffset.UtcNow);

        var result = await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", priority: 2));

        result.ShouldBe(RecurringScheduleUpsertResult.Updated);
        var schedule = await context.ReadScheduleAsync("schedule-a");
        schedule.IsPaused.ShouldBeFalse();
        schedule.NextFireAtUtc.ShouldNotBeNull();
        schedule.Priority.ShouldBe(2);
    }

    [Fact]
    public async Task CreateOrUpdateRecurringSchedule_UpdateOptionsCanPreservePausedStateAndNullNextFire()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", priority: 1));
        await context.Store.PauseRecurringScheduleAsync("schedule-a", DateTimeOffset.UtcNow);

        var result = await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule(
          "schedule-a",
          priority: 2,
          updateOptions: new RecurringScheduleUpdateOptions(OverwritePausedState: false)));

        result.ShouldBe(RecurringScheduleUpsertResult.Updated);
        var schedule = await context.ReadScheduleAsync("schedule-a");
        schedule.IsPaused.ShouldBeTrue();
        schedule.NextFireAtUtc.ShouldBeNull();
        schedule.Priority.ShouldBe(2);
    }

    [Fact]
    public async Task CreateOrUpdateRecurringSchedule_UpdateOptionsCanPreserveCronExpression()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", priority: 1));

        var result = await context.Store.CreateOrUpdateRecurringScheduleAsync(
          PostgresTestData.CreateSchedule(
            "schedule-a",
            priority: 2,
            updateOptions: new RecurringScheduleUpdateOptions(OverwriteCronExpression: false))
          with
          {
              CronExpression = "*/5 * * * *",
          });

        result.ShouldBe(RecurringScheduleUpsertResult.Updated);
        var schedule = await context.ReadScheduleAsync("schedule-a");
        schedule.CronExpression.ShouldBe("* * * * *");
        schedule.Priority.ShouldBe(2);
    }
}
