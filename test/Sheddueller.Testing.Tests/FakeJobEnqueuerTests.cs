namespace Sheddueller.Testing.Tests;

using Sheddueller.Serialization;
using Sheddueller.Storage;

using Shouldly;

public sealed class FakeJobEnqueuerTests
{
    [Fact]
    public async Task EnqueueMany_CyclicIdempotentGraph_ReportsIdempotencyBeforeCycle()
    {
        var fake = new FakeJobEnqueuer();
        var first = JobEnqueueItem.Create<TestJobService>(
          (service, ct) => service.HandleAsync(new SamplePayload("first", 1), ct),
          new JobSubmission(IdempotencyKind: JobIdempotencyKind.MethodAndArguments));
        var second = JobEnqueueItem.Create<TestJobService>((service, ct) => service.HandleAsync(new SamplePayload("second", 2), ct));
        first.DependsOn(second);
        second.DependsOn(first);

        var exception = await Should.ThrowAsync<ArgumentException>(() => fake.EnqueueManyAsync([first, second]).AsTask());

        exception.Message.ShouldStartWith("Jobs in a dependency graph cannot use idempotency.");
        exception.ParamName.ShouldBe("jobs");
        fake.EnqueuedJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Enqueue_ServiceMethodCall_RecordsJobAndMatchesByExpression()
    {
        var fake = new FakeJobEnqueuer();
        var payload = new SamplePayload("alpha", 42);
        var submission = new JobSubmission(
          Priority: 7,
          ConcurrencyGroupKeys: ["group-a", "group-a"],
          Tags: [new JobTag(" tenant ", " acme ")]);

        var jobId = await fake.EnqueueAsync<TestJobService>(
          (s, ct) => s.HandleAsync(payload, ct),
          submission);

        var match = await fake.MatchAsync<TestJobService>(
          (s, ct) => s.HandleAsync(new SamplePayload("alpha", 42), ct));

        AssertServiceMatch(match, jobId, payload);
    }

    private static void AssertServiceMatch(
      FakeJobMatch matchedJobs,
      Guid jobId,
      SamplePayload payload)
    {
        matchedJobs.Count.ShouldBe(1);
        matchedJobs[0].JobId.ShouldBe(jobId);
        matchedJobs[0].ServiceType.ShouldBe(typeof(TestJobService));
        matchedJobs[0].MethodName.ShouldBe(nameof(TestJobService.HandleAsync));
        matchedJobs[0].InvocationTargetKind.ShouldBe(JobInvocationTargetKind.Instance);
        matchedJobs[0].MethodParameterTypes.ShouldBe([typeof(SamplePayload), typeof(CancellationToken)]);
        matchedJobs[0].MethodParameterBindings.ShouldBe([
          new JobMethodParameterBinding(JobMethodParameterBindingKind.Serialized),
          new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
        ]);
        matchedJobs[0].SerializableArguments.ShouldBe([payload]);
        matchedJobs[0].Submission.Priority.ShouldBe(7);
        matchedJobs[0].Submission.ConcurrencyGroupKeys.ShouldBe(["group-a"]);
        matchedJobs[0].Submission.Tags.ShouldBe([new JobTag("tenant", "acme")]);
    }

    [Fact]
    public async Task Match_DifferentSerializedArguments_ReturnsEmptyMatch()
    {
        var fake = new FakeJobEnqueuer();

        await fake.EnqueueAsync<TestJobService>(
          (s, ct) => s.HandleAsync(new SamplePayload("alpha", 42), ct));

        var match = await fake.MatchAsync<TestJobService>(
          (s, ct) => s.HandleAsync(new SamplePayload("alpha", 43), ct));

        match.ShouldBeEmpty();
    }

    [Fact]
    public async Task Enqueue_StaticValueTaskMethod_RecordsJobAndMatchesByExpression()
    {
        var fake = new FakeJobEnqueuer();

        await fake.EnqueueAsync(
          ct => TestJobService.StaticValueTaskAsync("alpha", ct));

        var match = await fake.MatchAsync(
          ct => TestJobService.StaticValueTaskAsync("alpha", ct));

        match.Count.ShouldBe(1);
        match[0].ServiceType.ShouldBe(typeof(TestJobService));
        match[0].MethodName.ShouldBe(nameof(TestJobService.StaticValueTaskAsync));
        match[0].InvocationTargetKind.ShouldBe(JobInvocationTargetKind.Static);
    }

    [Fact]
    public async Task Enqueue_ProgressAwareServiceMethod_RecordsJobAndMatchesByExpression()
    {
        var fake = new FakeJobEnqueuer();
        var payload = new SamplePayload("alpha", 42);

        var jobId = await fake.EnqueueAsync<TestJobService>(
          (s, ct, progress) => s.HandleWithProgressAsync(payload, progress, ct));

        var match = await fake.MatchAsync<TestJobService>(
          (s, ct, progress) => s.HandleWithProgressAsync(new SamplePayload("alpha", 42), progress, ct));

        match.Count.ShouldBe(1);
        match[0].JobId.ShouldBe(jobId);
        match[0].MethodName.ShouldBe(nameof(TestJobService.HandleWithProgressAsync));
        match[0].MethodParameterTypes.ShouldBe([typeof(SamplePayload), typeof(IProgress<decimal>), typeof(CancellationToken)]);
        match[0].MethodParameterBindings.ShouldBe([
          new JobMethodParameterBinding(JobMethodParameterBindingKind.Serialized),
          new JobMethodParameterBinding(JobMethodParameterBindingKind.ProgressReporter),
          new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
        ]);
        match[0].SerializableArguments.ShouldBe([payload]);
    }

    [Fact]
    public async Task EnqueueMany_MixedJobs_RecordsBatchMetadataAndReturnsIdsInInputOrder()
    {
        var fake = new FakeJobEnqueuer();

        var jobIds = await fake.EnqueueManyAsync([
          JobEnqueueItem.Create<TestJobService>(
            (s, ct) => s.HandleAsync(new SamplePayload("alpha", 42), ct)),
          JobEnqueueItem.Create(
            ct => TestJobService.StaticTaskAsync("beta", ct)),
        ]);

        fake.EnqueuedJobs.Count.ShouldBe(2);
        jobIds.ShouldBe([fake.EnqueuedJobs[0].JobId, fake.EnqueuedJobs[1].JobId]);
        fake.EnqueuedJobs[0].BatchId.ShouldNotBeNull();
        fake.EnqueuedJobs[1].BatchId.ShouldBe(fake.EnqueuedJobs[0].BatchId);
        fake.EnqueuedJobs[0].BatchIndex.ShouldBe(0);
        fake.EnqueuedJobs[1].BatchIndex.ShouldBe(1);
        fake.EnqueuedJobs[0].EnqueueSequence.ShouldBe(0);
        fake.EnqueuedJobs[1].EnqueueSequence.ShouldBe(1);
    }

    [Fact]
    public async Task EnqueueMany_DependencyGraph_RecordsPrerequisiteJobIds()
    {
        var fake = new FakeJobEnqueuer();
        var first = JobEnqueueItem.Create<TestJobService>(
          (service, cancellationToken) => service.HandleAsync(new SamplePayload("first", 1), cancellationToken));
        var second = JobEnqueueItem.Create<TestJobService>(
          (service, cancellationToken) => service.HandleAsync(new SamplePayload("second", 2), cancellationToken));
        var aggregate = JobEnqueueItem.Create(
            cancellationToken => TestJobService.StaticTaskAsync("aggregate", cancellationToken))
          .DependsOn([first, second]);

        var jobIds = await fake.EnqueueManyAsync([first, second, aggregate]);

        fake.EnqueuedJobs[0].PrerequisiteJobIds.ShouldBeEmpty();
        fake.EnqueuedJobs[1].PrerequisiteJobIds.ShouldBeEmpty();
        fake.EnqueuedJobs[2].PrerequisiteJobIds.ShouldBe([jobIds[0], jobIds[1]]);
    }

    [Fact]
    public async Task EnqueueMany_InvalidItem_DoesNotRecordAnyJobs()
    {
        var fake = new FakeJobEnqueuer();

        await Should.ThrowAsync<ArgumentException>(
          () => fake.EnqueueManyAsync([
            JobEnqueueItem.Create<TestJobService>(
              (s, ct) => s.HandleAsync(new SamplePayload("alpha", 42), ct)),
            JobEnqueueItem.Create<TestJobService>(
              (s, ct) => s.NoTokenAsync()),
          ]).AsTask());

        fake.EnqueuedJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Match_CustomSerializer_MatchesBySerializedPayload()
    {
        var fake = new FakeJobEnqueuer(new ConstantPayloadSerializer());

        await fake.EnqueueAsync<TestJobService>(
          (s, ct) => s.HandleAsync(new SamplePayload("alpha", 42), ct));

        var match = await fake.MatchAsync<TestJobService>(
          (s, ct) => s.HandleAsync(new SamplePayload("beta", 99), ct));

        match.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SerializedArguments_ReturnedPayload_IsDefensiveCopy()
    {
        var fake = new FakeJobEnqueuer();

        await fake.EnqueueAsync<TestJobService>(
          (s, ct) => s.HandleStringAsync("alpha", ct));

        var payload = fake.EnqueuedJobs[0].SerializedArguments;
        payload.Data[0] = 0;

        var match = await fake.MatchAsync<TestJobService>(
          (s, ct) => s.HandleStringAsync("alpha", ct));

        match.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Clear_RecordedJobs_RemovesHistoryAndResetsSequence()
    {
        var fake = new FakeJobEnqueuer();

        await fake.EnqueueAsync<TestJobService>(
          (s, ct) => s.HandleStringAsync("alpha", ct));

        fake.Clear();
        await fake.EnqueueAsync<TestJobService>(
          (s, ct) => s.HandleStringAsync("beta", ct));

        fake.EnqueuedJobs.Count.ShouldBe(1);
        fake.EnqueuedJobs[0].EnqueueSequence.ShouldBe(0);
        (await fake.MatchAsync<TestJobService>(
          (s, ct) => s.HandleStringAsync("alpha", ct))).ShouldBeEmpty();
    }

    private sealed record SamplePayload(string Name, int Count);

    private sealed class TestJobService
    {
        public Task HandleAsync(SamplePayload payload, CancellationToken cancellationToken)
          => Task.CompletedTask;

        public Task HandleStringAsync(string value, CancellationToken cancellationToken)
          => Task.CompletedTask;

        public Task HandleWithProgressAsync(SamplePayload payload, IProgress<decimal> progress, CancellationToken cancellationToken)
          => Task.CompletedTask;

        public Task NoTokenAsync()
          => Task.CompletedTask;

        public static Task StaticTaskAsync(string value, CancellationToken cancellationToken)
          => Task.CompletedTask;

        public static ValueTask StaticValueTaskAsync(string value, CancellationToken cancellationToken)
          => ValueTask.CompletedTask;
    }

    private sealed class ConstantPayloadSerializer : IJobPayloadSerializer
    {
        public ValueTask<SerializedJobPayload> SerializeAsync(
          IReadOnlyList<object?> arguments,
          IReadOnlyList<Type> parameterTypes,
          CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new SerializedJobPayload("test/constant", [1, 2, 3]));

        public ValueTask<IReadOnlyList<object?>> DeserializeAsync(
          SerializedJobPayload payload,
          IReadOnlyList<Type> parameterTypes,
          CancellationToken cancellationToken = default)
          => ValueTask.FromResult<IReadOnlyList<object?>>([]);
    }
}
