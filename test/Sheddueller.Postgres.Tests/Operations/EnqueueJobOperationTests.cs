namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Serialization;
using Sheddueller.Storage;

using Shouldly;

public sealed class EnqueueJobOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Enqueue_PersistsJobFieldsAndGroupRows()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        var notBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(5);

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          priority: 7,
          enqueuedAtUtc: new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero),
          notBeforeUtc: notBeforeUtc,
          maxAttempts: 3,
          retryBackoffKind: RetryBackoffKind.Exponential,
          retryBaseDelay: TimeSpan.FromSeconds(2),
          retryMaxDelay: TimeSpan.FromSeconds(9),
          groupKeys: ["beta", "alpha", "alpha"]));

        var job = await context.ReadJobAsync(jobId);

        job.State.ShouldBe("Queued");
        job.Priority.ShouldBe(7);
        job.EnqueuedAtUtc.Year.ShouldNotBe(1999);
        job.NotBeforeUtc.ShouldNotBeNull().ShouldBe(notBeforeUtc, TimeSpan.FromMilliseconds(1));
        job.ServiceType.ShouldBe(PostgresTestData.ServiceType);
        job.MethodName.ShouldBe(PostgresTestData.MethodName);
        job.MethodParameterTypes.ShouldBe([typeof(CancellationToken).AssemblyQualifiedName!]);
        job.InvocationTargetKind.ShouldBe("Instance");
        job.MethodParameterBindings.ShouldBeNull();
        job.SerializedArgumentsContentType.ShouldBe(SystemTextJsonJobPayloadSerializer.JsonContentType);
        job.SerializedArguments.ShouldBe("[]"u8.ToArray());
        job.AttemptCount.ShouldBe(0);
        job.MaxAttempts.ShouldBe(3);
        job.RetryBackoffKind.ShouldBe("Exponential");
        job.RetryBaseDelayMs.ShouldBe(2000);
        job.RetryMaxDelayMs.ShouldBe(9000);
        (await context.ReadJobGroupKeysAsync(jobId)).ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public async Task Enqueue_SingleJobAndBatch_WriteTheSameRowsEventsAndMetrics()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var single = Guid.NewGuid();
        var batched = Guid.NewGuid();
        static EnqueueJobRequest CreateRequest(Guid jobId)
          => PostgresTestData.CreateRequest(
            jobId,
            priority: 4,
            notBeforeUtc: DateTimeOffset.UtcNow.AddMinutes(5),
            maxAttempts: 2,
            retryBackoffKind: RetryBackoffKind.Fixed,
            retryBaseDelay: TimeSpan.FromSeconds(3),
            groupKeys: ["beta", "alpha", "alpha"],
            tags: [new JobTag(" tenant ", "acme"), new JobTag("area", "billing"), new JobTag("tenant", "acme")],
            invocationTargetKind: JobInvocationTargetKind.Static,
            methodParameterBindings: [new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken)]);

        (await context.Store.EnqueueAsync(CreateRequest(single))).WasEnqueued.ShouldBeTrue();
        (await context.Store.EnqueueManyAsync([CreateRequest(batched), PostgresTestData.CreateRequest(Guid.NewGuid())]))
          .ShouldAllBe(result => result.WasEnqueued);

        var singleJob = await context.ReadJobAsync(single);
        var batchedJob = await context.ReadJobAsync(batched);
        batchedJob.State.ShouldBe(singleJob.State);
        batchedJob.Priority.ShouldBe(singleJob.Priority);
        batchedJob.ServiceType.ShouldBe(singleJob.ServiceType);
        batchedJob.MethodName.ShouldBe(singleJob.MethodName);
        batchedJob.MethodParameterTypes.ShouldBe(singleJob.MethodParameterTypes);
        batchedJob.InvocationTargetKind.ShouldBe(singleJob.InvocationTargetKind);
        batchedJob.MethodParameterBindings.ShouldBe(singleJob.MethodParameterBindings);
        batchedJob.SerializedArgumentsContentType.ShouldBe(singleJob.SerializedArgumentsContentType);
        batchedJob.SerializedArguments.ShouldBe(singleJob.SerializedArguments);
        batchedJob.AttemptCount.ShouldBe(singleJob.AttemptCount);
        batchedJob.MaxAttempts.ShouldBe(singleJob.MaxAttempts);
        batchedJob.RetryBackoffKind.ShouldBe(singleJob.RetryBackoffKind);
        batchedJob.RetryBaseDelayMs.ShouldBe(singleJob.RetryBaseDelayMs);
        batchedJob.RetryMaxDelayMs.ShouldBe(singleJob.RetryMaxDelayMs);
        batchedJob.SourceScheduleKey.ShouldBe(singleJob.SourceScheduleKey);
        batchedJob.ScheduleOccurrenceKind.ShouldBe(singleJob.ScheduleOccurrenceKind);
        batchedJob.IdempotencyKey.ShouldBe(singleJob.IdempotencyKey);
        singleJob.NotBeforeUtc.ShouldNotBeNull();
        batchedJob.NotBeforeUtc.ShouldNotBeNull();

        foreach (var jobId in new[] { single, batched })
        {
            (await context.ReadJobGroupKeysAsync(jobId)).ShouldBe(["alpha", "beta"]);
            (await context.ReadJobTagsAsync(jobId)).ShouldBe([new JobTag("tenant", "acme"), new JobTag("area", "billing")]);
            (await context.ReadJobEventSequenceAsync(jobId)).ShouldBe(1);
            var jobEvent = (await context.ReadJobEventsAsync(jobId)).ShouldHaveSingleItem();
            jobEvent.EventSequence.ShouldBe(1);
            jobEvent.Kind.ShouldBe(JobEventKind.Lifecycle);
            jobEvent.AttemptNumber.ShouldBe(0);
            jobEvent.Message.ShouldBe("Queued");
            jobEvent.LogLevel.ShouldBeNull();
            jobEvent.ProgressPercent.ShouldBeNull();
            jobEvent.Fields.ShouldBeNull();
        }

        (await context.SumEnqueuedMetricAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Enqueue_IdempotencyKey_PersistsJobField()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, idempotencyKey: "listing:3"));

        var job = await context.ReadJobAsync(jobId);

        job.IdempotencyKey.ShouldBe("listing:3");
    }

    [Fact]
    public async Task Enqueue_InvokeMetadata_PersistsTargetKindAndParameterBindings()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          invocationTargetKind: JobInvocationTargetKind.Static,
          methodParameterBindings:
          [
              new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
          ]));

        var job = await context.ReadJobAsync(jobId);

        job.InvocationTargetKind.ShouldBe("Static");
        job.MethodParameterBindings.ShouldBe(["CancellationToken"]);
    }

    [Fact]
    public async Task Enqueue_AssignsIncreasingSequences()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var firstResult = await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(first));
        var secondResult = await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(second));

        secondResult.EnqueueSequence.ShouldBeGreaterThan(firstResult.EnqueueSequence);
        (await context.ReadJobAsync(first)).EnqueueSequence.ShouldBe(firstResult.EnqueueSequence);
        (await context.ReadJobAsync(second)).EnqueueSequence.ShouldBe(secondResult.EnqueueSequence);
    }

    [Fact]
    public async Task EnqueueMany_PersistsJobsGroupsTagsAndEventsAtomically()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var results = await context.Store.EnqueueManyAsync([
          PostgresTestData.CreateRequest(
            first,
            priority: 7,
            groupKeys: ["beta", "alpha", "alpha"],
            tags: [new JobTag("tenant", "acme"), new JobTag("domain", "payments"), new JobTag("tenant", "acme")]),
          PostgresTestData.CreateRequest(
            second,
            priority: 2,
            groupKeys: ["gamma"],
            tags: [new JobTag("kind", "secondary")]),
        ]);

        results.Select(result => result.JobId).ShouldBe([first, second]);
        results[1].EnqueueSequence.ShouldBeGreaterThan(results[0].EnqueueSequence);

        var firstJob = await context.ReadJobAsync(first);
        var secondJob = await context.ReadJobAsync(second);
        firstJob.State.ShouldBe("Queued");
        secondJob.State.ShouldBe("Queued");
        firstJob.EnqueueSequence.ShouldBe(results[0].EnqueueSequence);
        secondJob.EnqueueSequence.ShouldBe(results[1].EnqueueSequence);
        (await context.ReadJobGroupKeysAsync(first)).ShouldBe(["alpha", "beta"]);
        (await context.ReadJobGroupKeysAsync(second)).ShouldBe(["gamma"]);
        (await context.ReadJobTagsAsync(first)).ShouldBe([new JobTag("tenant", "acme"), new JobTag("domain", "payments")]);
        (await context.ReadJobTagsAsync(second)).ShouldBe([new JobTag("kind", "secondary")]);
        (await context.CountJobEventsAsync(first)).ShouldBe(1);
        (await context.CountJobEventsAsync(second)).ShouldBe(1);
    }

    [Fact]
    public async Task EnqueueMany_DuplicateJobId_RollsBackWholeBatch()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var duplicate = Guid.NewGuid();
        var other = Guid.NewGuid();

        await Should.ThrowAsync<Exception>(
          () => context.Store.EnqueueManyAsync([
            PostgresTestData.CreateRequest(other),
            PostgresTestData.CreateRequest(duplicate),
            PostgresTestData.CreateRequest(duplicate),
          ]).AsTask());

        (await context.CountJobsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Enqueue_ConcurrentSameIdempotencyKey_PersistsSingleJob()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
          .Select(_ => context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), idempotencyKey: "listing:3")).AsTask()));

        results.Select(result => result.JobId).Distinct().Count().ShouldBe(1);
        results.Count(result => result.WasEnqueued).ShouldBe(1);
        (await context.CountJobsAsync()).ShouldBe(1);
    }
}
