namespace Sheddueller.Testing.Tests;

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using Sheddueller.Storage;

using Shouldly;

/// <summary>
/// The fakes must reject invalid input exactly as the real services do, so a test written against a fake fails where
/// production would. Several scenarios combine two faults, which pins down the order the checks run in.
/// </summary>
public sealed class FakeValidationParityTests
{
    private const string EveryMinute = "* * * * *";

    private static readonly RetryPolicy InvalidRetryPolicy = new(0, RetryBackoffKind.Fixed, TimeSpan.FromSeconds(1));

    private static readonly JobSubmission IdempotentSubmission = new(IdempotencyKind: JobIdempotencyKind.MethodAndArguments);

    private static readonly Dictionary<string, Func<IJobEnqueuer, ValueTask>> InvalidSubmissions = new(StringComparer.Ordinal)
    {
        ["work is null"] = async enqueuer => await enqueuer.EnqueueAsync((Expression<Func<CancellationToken, Task>>)null!),
        ["work is not a method call"] = async enqueuer => await enqueuer.EnqueueAsync(cancellationToken => Task.CompletedTask),
        ["scheduler token not forwarded"] = async enqueuer => await enqueuer.EnqueueAsync<ParityJobs>(
            (jobs, cancellationToken) => jobs.RunAsync("a", CancellationToken.None)),
        ["target is not the service"] = async enqueuer => await enqueuer.EnqueueAsync<ParityJobs>(
            (jobs, cancellationToken) => new ParityJobs().RunAsync("a", cancellationToken)),
        ["empty concurrency group"] = async enqueuer => await EnqueueAsync(enqueuer, new JobSubmission(ConcurrencyGroupKeys: [string.Empty])),
        ["blank tag name"] = async enqueuer => await EnqueueAsync(enqueuer, new JobSubmission(Tags: [new JobTag(" ", "value")])),
        ["invalid retry policy"] = async enqueuer => await EnqueueAsync(enqueuer, new JobSubmission(RetryPolicy: InvalidRetryPolicy)),
        ["idempotent job delayed"] = async enqueuer => await EnqueueAsync(enqueuer, IdempotentSubmission with { NotBeforeUtc = DateTimeOffset.UnixEpoch }),
        ["unknown idempotency kind"] = async enqueuer => await EnqueueAsync(enqueuer, new JobSubmission(IdempotencyKind: (JobIdempotencyKind)99)),
        ["blank tag and invalid retry policy"] = async enqueuer => await EnqueueAsync(
            enqueuer,
            new JobSubmission(RetryPolicy: InvalidRetryPolicy, Tags: [new JobTag(" ", "value")])),
        ["empty concurrency group and blank tag"] = async enqueuer => await EnqueueAsync(
            enqueuer,
            new JobSubmission(ConcurrencyGroupKeys: [string.Empty], Tags: [new JobTag(" ", "value")])),
        ["idempotent job delayed with empty concurrency group"] = async enqueuer => await EnqueueAsync(
            enqueuer,
            IdempotentSubmission with { NotBeforeUtc = DateTimeOffset.UnixEpoch, ConcurrencyGroupKeys = [string.Empty] }),
        ["batch is null"] = async enqueuer => await enqueuer.EnqueueManyAsync(null!),
        ["batch item is null"] = async enqueuer => await enqueuer.EnqueueManyAsync([CreateItem("a"), null!]),
        ["batch item repeated"] = async enqueuer =>
        {
            var item = CreateItem("a");
            await enqueuer.EnqueueManyAsync([item, item]);
        },
        ["prerequisite outside batch"] = async enqueuer => await enqueuer.EnqueueManyAsync([CreateItem("b").DependsOn(CreateItem("a"))]),
        ["dependency cycle"] = async enqueuer =>
        {
            var first = CreateItem("a");
            var second = CreateItem("b").DependsOn(first);
            first.DependsOn(second);
            await enqueuer.EnqueueManyAsync([first, second]);
        },
        ["idempotent dependency graph"] = async enqueuer =>
        {
            var first = CreateItem("a", IdempotentSubmission);
            await enqueuer.EnqueueManyAsync([first, CreateItem("b").DependsOn(first)]);
        },
        ["idempotent dependency graph with prerequisite outside batch"] = async enqueuer =>
            await enqueuer.EnqueueManyAsync([CreateItem("a", IdempotentSubmission), CreateItem("b").DependsOn(CreateItem("c"))]),
        ["batch item work invalid"] = async enqueuer => await enqueuer.EnqueueManyAsync([
            CreateItem("a"),
            JobEnqueueItem.Create<ParityJobs>((jobs, cancellationToken) => jobs.RunAsync("b", CancellationToken.None)),
        ]),
        ["batch item submission invalid"] = async enqueuer => await enqueuer.EnqueueManyAsync([
            CreateItem("a"),
            CreateItem("b", new JobSubmission(ConcurrencyGroupKeys: [string.Empty])),
        ]),
    };

    private static readonly Dictionary<string, Func<IRecurringScheduleManager, ValueTask>> InvalidSchedules = new(StringComparer.Ordinal)
    {
        ["empty schedule key"] = async manager => await UpsertAsync(manager, scheduleKey: string.Empty),
        ["invalid cron expression"] = async manager => await UpsertAsync(manager, cronExpression: "not cron"),
        ["work is null"] = async manager => await manager.CreateOrUpdateAsync(
            "schedule",
            EveryMinute,
            (Expression<Func<CancellationToken, Task>>)null!),
        ["work is not a method call"] = async manager => await manager.CreateOrUpdateAsync(
            "schedule",
            EveryMinute,
            cancellationToken => Task.CompletedTask),
        ["empty schedule key and null work"] = async manager => await manager.CreateOrUpdateAsync(
            string.Empty,
            EveryMinute,
            (Expression<Func<CancellationToken, Task>>)null!),
        ["empty schedule key and invalid work"] = async manager => await manager.CreateOrUpdateAsync<ParityJobs>(
            string.Empty,
            EveryMinute,
            (jobs, cancellationToken) => jobs.RunAsync("a", CancellationToken.None)),
        ["invalid cron expression and invalid work"] = async manager => await manager.CreateOrUpdateAsync<ParityJobs>(
            "schedule",
            "not cron",
            (jobs, cancellationToken) => jobs.RunAsync("a", CancellationToken.None)),
        ["empty concurrency group"] = async manager => await UpsertAsync(
            manager,
            options: new RecurringScheduleOptions(ConcurrencyGroupKeys: [string.Empty])),
        ["blank tag name"] = async manager => await UpsertAsync(
            manager,
            options: new RecurringScheduleOptions(Tags: [new JobTag(" ", "value")])),
        ["invalid retry policy"] = async manager => await UpsertAsync(
            manager,
            options: new RecurringScheduleOptions(RetryPolicy: InvalidRetryPolicy)),
        ["blank tag and invalid retry policy"] = async manager => await UpsertAsync(
            manager,
            options: new RecurringScheduleOptions(RetryPolicy: InvalidRetryPolicy, Tags: [new JobTag(" ", "value")])),
        ["invalid work and empty concurrency group"] = async manager => await manager.CreateOrUpdateAsync<ParityJobs>(
            "schedule",
            EveryMinute,
            (jobs, cancellationToken) => jobs.RunAsync("a", CancellationToken.None),
            new RecurringScheduleOptions(ConcurrencyGroupKeys: [string.Empty])),
        ["trigger with empty schedule key"] = async manager => await manager.TriggerAsync(string.Empty),
        ["delete with empty schedule key"] = async manager => await manager.DeleteAsync(string.Empty),
        ["pause with empty schedule key"] = async manager => await manager.PauseAsync(string.Empty),
        ["resume with empty schedule key"] = async manager => await manager.ResumeAsync(string.Empty),
        ["get with empty schedule key"] = async manager => await manager.GetAsync(string.Empty),
    };

    public static TheoryData<string> InvalidSubmissionScenarios
      => [.. InvalidSubmissions.Keys];

    public static TheoryData<string> InvalidScheduleScenarios
      => [.. InvalidSchedules.Keys];

    [Theory]
    [MemberData(nameof(InvalidSubmissionScenarios))]
    public async Task InvalidSubmission_FakeEnqueuer_ThrowsLikeRealEnqueuer(string scenario)
    {
        var submit = InvalidSubmissions[scenario];
        await using var provider = CreateRealServices();

        var expected = await Should.ThrowAsync<Exception>(async () => await submit(provider.GetRequiredService<IJobEnqueuer>()));
        var actual = await Should.ThrowAsync<Exception>(async () => await submit(new FakeJobEnqueuer()));

        actual.GetType().ShouldBe(expected.GetType());
        actual.Message.ShouldBe(expected.Message);
    }

    [Theory]
    [MemberData(nameof(InvalidScheduleScenarios))]
    public async Task InvalidSchedule_FakeScheduleManager_ThrowsLikeRealManager(string scenario)
    {
        var upsert = InvalidSchedules[scenario];
        await using var provider = CreateRealServices();

        var expected = await Should.ThrowAsync<Exception>(async () => await upsert(provider.GetRequiredService<IRecurringScheduleManager>()));
        var actual = await Should.ThrowAsync<Exception>(async () => await upsert(new FakeRecurringScheduleManager()));

        actual.GetType().ShouldBe(expected.GetType());
        actual.Message.ShouldBe(expected.Message);
    }

    private static ServiceProvider CreateRealServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(DispatchProxy.Create<IJobStore, UnreachableJobStore>());
        services.AddSheddueller();

        return services.BuildServiceProvider();
    }

    private static ValueTask<Guid> EnqueueAsync(IJobEnqueuer enqueuer, JobSubmission submission)
      => enqueuer.EnqueueAsync<ParityJobs>((jobs, cancellationToken) => jobs.RunAsync("a", cancellationToken), submission);

    private static ValueTask<RecurringScheduleUpsertResult> UpsertAsync(
        IRecurringScheduleManager manager,
        string scheduleKey = "schedule",
        string cronExpression = EveryMinute,
        RecurringScheduleOptions? options = null)
      => manager.CreateOrUpdateAsync<ParityJobs>(
          scheduleKey,
          cronExpression,
          (jobs, cancellationToken) => jobs.RunAsync("a", cancellationToken),
          options);

    private static JobEnqueueItem CreateItem(string value, JobSubmission? submission = null)
      => JobEnqueueItem.Create<ParityJobs>((jobs, cancellationToken) => jobs.RunAsync(value, cancellationToken), submission);

    private sealed class ParityJobs
    {
        public Task RunAsync(string value, CancellationToken cancellationToken)
          => Task.CompletedTask;
    }

    /// <summary>
    /// The real services reject invalid input before using the store, so reaching it is a test failure.
    /// </summary>
    [SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy derives the store proxy from this type at runtime.")]
    private class UnreachableJobStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
          => throw new InvalidOperationException($"Invalid input reached the job store through {targetMethod?.Name}.");
    }
}
