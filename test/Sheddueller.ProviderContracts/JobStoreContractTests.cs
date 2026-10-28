namespace Sheddueller.ProviderContracts;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Serialization;
using Sheddueller.Storage;

using Shouldly;

public abstract class JobStoreContractTests
{
    protected static readonly DateTimeOffset ContractClock = new(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);

    protected abstract ValueTask<JobStoreContractContext> CreateContextAsync();

    [Fact]
    public async Task EnqueuedJob_Claim_RoundTripsSubmittedMetadata()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();
        var request = CreateRequest(
          jobId,
          priority: 7,
          maxAttempts: 3,
          retryBackoffKind: RetryBackoffKind.Exponential,
          retryBaseDelay: TimeSpan.FromMilliseconds(50),
          retryMaxDelay: TimeSpan.FromMilliseconds(75),
          groupKeys: ["alpha", "beta"]);

        var enqueueResult = await context.Store.EnqueueAsync(request);
        var claimed = await ClaimAsync(context.Store);

        enqueueResult.JobId.ShouldBe(jobId);
        enqueueResult.EnqueueSequence.ShouldBeGreaterThan(0);
        claimed.JobId.ShouldBe(jobId);
        claimed.Priority.ShouldBe(7);
        claimed.ServiceType.ShouldBe(request.ServiceType);
        claimed.MethodName.ShouldBe(request.MethodName);
        claimed.MethodParameterTypes.ShouldBe(request.MethodParameterTypes);
        claimed.SerializedArguments.ContentType.ShouldBe(request.SerializedArguments.ContentType);
        claimed.SerializedArguments.Data.ShouldBe(request.SerializedArguments.Data);
        claimed.ConcurrencyGroupKeys.ShouldBe(["alpha", "beta"]);
        claimed.AttemptCount.ShouldBe(1);
        claimed.MaxAttempts.ShouldBe(3);
        claimed.RetryBackoffKind.ShouldBe(RetryBackoffKind.Exponential);
        claimed.RetryBaseDelay.ShouldBe(TimeSpan.FromMilliseconds(50));
        claimed.RetryMaxDelay.ShouldBe(TimeSpan.FromMilliseconds(75));
    }

    [Fact]
    public async Task TryClaimNext_PriorityAndFifo_ClaimsHigherPriorityThenOldest()
    {
        await using var context = await this.CreateContextAsync();
        var firstLow = Guid.NewGuid();
        var secondLow = Guid.NewGuid();
        var high = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(firstLow, priority: 0));
        await context.Store.EnqueueAsync(CreateRequest(secondLow, priority: 0));
        await context.Store.EnqueueAsync(CreateRequest(high, priority: 10));

        (await ClaimAsync(context.Store)).JobId.ShouldBe(high);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(firstLow);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(secondLow);
    }

    [Fact]
    public async Task EnqueueMany_PriorityAndFifo_ClaimsHigherPriorityThenOldestBatchItem()
    {
        await using var context = await this.CreateContextAsync();
        var firstLow = Guid.NewGuid();
        var secondLow = Guid.NewGuid();
        var high = Guid.NewGuid();

        var results = await context.Store.EnqueueManyAsync([
          CreateRequest(firstLow, priority: 0),
          CreateRequest(secondLow, priority: 0),
          CreateRequest(high, priority: 10),
        ]);

        results.Select(result => result.JobId).ShouldBe([firstLow, secondLow, high]);
        results[1].EnqueueSequence.ShouldBeGreaterThan(results[0].EnqueueSequence);
        results[2].EnqueueSequence.ShouldBeGreaterThan(results[1].EnqueueSequence);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(high);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(firstLow);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(secondLow);
    }

    [Fact]
    public async Task EnqueueMany_EmptyBatch_ReturnsEmptyWithoutPersistingJob()
    {
        await using var context = await this.CreateContextAsync();

        var results = await context.Store.EnqueueManyAsync([]);

        results.ShouldBeEmpty();
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task DependencyGraph_PrerequisitesBecomeTerminal_DependentBecomesClaimable()
    {
        await using var context = await this.CreateContextAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var dependent = Guid.NewGuid();
        await context.Store.EnqueueManyAsync([
          CreateRequest(first),
          CreateRequest(second),
          CreateRequest(dependent, priority: 100, prerequisiteJobIds: [first, second]),
        ]);

        var firstClaim = await ClaimAsync(context.Store);
        firstClaim.JobId.ShouldBe(first);
        (await context.Store.MarkCompletedAsync(
          new CompleteJobRequest(first, "node-1", firstClaim.LeaseToken, ContractClock))).ShouldBeTrue();

        var secondClaim = await ClaimAsync(context.Store);
        secondClaim.JobId.ShouldBe(second);
        (await context.Store.MarkFailedAsync(
          new FailJobRequest(
            second,
            "node-1",
            secondClaim.LeaseToken,
            ContractClock,
            new JobFailureInfo("TestFailure", "failed", null)))).ShouldBeTrue();

        (await ClaimAsync(context.Store)).JobId.ShouldBe(dependent);
    }

    [Fact]
    public async Task DependencyGraph_UnfinishedPrerequisite_InspectionReportsWaitingForDependencies()
    {
        await using var context = await this.CreateContextAsync();
        var prerequisite = Guid.NewGuid();
        var dependent = Guid.NewGuid();
        await context.Store.EnqueueManyAsync([
          CreateRequest(prerequisite),
          CreateRequest(dependent, priority: 100, prerequisiteJobIds: [prerequisite]),
        ]);

        var position = await GetInspectionReader(context).GetQueuePositionAsync(dependent);
        var detail = await GetInspectionReader(context).GetJobAsync(dependent);

        position.Kind.ShouldBe(JobQueuePositionKind.WaitingForDependencies);
        detail.ShouldNotBeNull().PrerequisiteJobIds.ShouldBe([prerequisite]);
    }

    [Fact]
    public async Task EnqueueMany_DuplicateJobIdInBatch_ThrowsWithoutPersistingAnyBatchItem()
    {
        await using var context = await this.CreateContextAsync();
        var duplicate = Guid.NewGuid();
        var other = Guid.NewGuid();

        await Should.ThrowAsync<Exception>(
          () => context.Store.EnqueueManyAsync([
            CreateRequest(other, priority: 10),
            CreateRequest(duplicate, priority: 0),
            CreateRequest(duplicate, priority: 0),
          ]).AsTask());

        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task EnqueueMany_ExistingJobId_ThrowsWithoutPersistingAnyBatchItem()
    {
        await using var context = await this.CreateContextAsync();
        var existing = Guid.NewGuid();
        var newHighPriority = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(existing, priority: 0));

        await Should.ThrowAsync<Exception>(
          () => context.Store.EnqueueManyAsync([
            CreateRequest(newHighPriority, priority: 10),
            CreateRequest(existing, priority: 0),
          ]).AsTask());

        (await ClaimAsync(context.Store)).JobId.ShouldBe(existing);
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task IdempotencyKey_QueuedJob_ReturnsExistingJobWithoutPersistingDuplicate()
    {
        await using var context = await this.CreateContextAsync();
        var existing = Guid.NewGuid();
        var duplicate = Guid.NewGuid();

        var firstResult = await context.Store.EnqueueAsync(CreateRequest(existing, idempotencyKey: "listing:3"));
        var secondResult = await context.Store.EnqueueAsync(CreateRequest(duplicate, priority: 100, idempotencyKey: "listing:3"));

        secondResult.JobId.ShouldBe(existing);
        secondResult.EnqueueSequence.ShouldBe(firstResult.EnqueueSequence);
        secondResult.WasEnqueued.ShouldBeFalse();
        (await ClaimAsync(context.Store)).JobId.ShouldBe(existing);
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
        (await GetInspectionReader(context).GetJobAsync(duplicate)).ShouldBeNull();
    }

    [Fact]
    public async Task IdempotencyKey_ClaimedJob_DoesNotSuppressNewEnqueue()
    {
        await using var context = await this.CreateContextAsync();
        var running = Guid.NewGuid();
        var next = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(running, idempotencyKey: "listing:3"));
        (await ClaimAsync(context.Store)).JobId.ShouldBe(running);

        var nextResult = await context.Store.EnqueueAsync(CreateRequest(next, idempotencyKey: "listing:3"));

        nextResult.JobId.ShouldBe(next);
        nextResult.WasEnqueued.ShouldBeTrue();
        (await ClaimAsync(context.Store)).JobId.ShouldBe(next);
    }

    [Fact]
    public async Task IdempotencyKey_RetryWaitingJob_ReturnsRetryJobWithoutPersistingDuplicate()
    {
        await using var context = await this.CreateContextAsync();
        var retrying = Guid.NewGuid();
        var duplicate = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(
          retrying,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1),
          idempotencyKey: "listing:3"));
        var claimed = await ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(retrying, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeTrue();

        var duplicateResult = await context.Store.EnqueueAsync(CreateRequest(duplicate, idempotencyKey: "listing:3"));

        duplicateResult.JobId.ShouldBe(retrying);
        duplicateResult.WasEnqueued.ShouldBeFalse();
        (await GetInspectionReader(context).GetJobAsync(duplicate)).ShouldBeNull();
    }

    [Fact]
    public async Task IdempotencyKey_TerminalJob_AllowsNewEnqueue()
    {
        await using var context = await this.CreateContextAsync();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(failed, idempotencyKey: "listing:3"));
        var claimed = await ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(failed, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeTrue();

        var replacementResult = await context.Store.EnqueueAsync(CreateRequest(replacement, idempotencyKey: "listing:3"));

        replacementResult.JobId.ShouldBe(replacement);
        replacementResult.WasEnqueued.ShouldBeTrue();
        (await ClaimAsync(context.Store)).JobId.ShouldBe(replacement);
    }

    [Fact]
    public async Task EnqueueMany_DuplicateIdempotencyKey_ReturnsRepresentativeWithoutPersistingDuplicate()
    {
        await using var context = await this.CreateContextAsync();
        var first = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var other = Guid.NewGuid();

        var results = await context.Store.EnqueueManyAsync([
          CreateRequest(first, idempotencyKey: "listing:3"),
          CreateRequest(duplicate, priority: 100, idempotencyKey: "listing:3"),
          CreateRequest(other, idempotencyKey: "listing:4"),
        ]);

        results.Select(result => result.JobId).ShouldBe([first, first, other]);
        results.Select(result => result.WasEnqueued).ShouldBe([true, false, true]);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(first);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(other);
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
        (await GetInspectionReader(context).GetJobAsync(duplicate)).ShouldBeNull();
    }

    [Fact]
    public async Task IdempotencyKey_DelayedRequest_ThrowsWithoutPersistingJob()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();

        await Should.ThrowAsync<ArgumentException>(
          () => context.Store.EnqueueAsync(CreateRequest(
            jobId,
            notBeforeUtc: DateTimeOffset.UtcNow.AddMinutes(1),
            idempotencyKey: "listing:3")).AsTask());

        (await GetInspectionReader(context).GetJobAsync(jobId)).ShouldBeNull();
    }

    [Fact]
    public async Task IdempotencyKey_ClaimedJobFailsWithQueuedReplacement_MarksClaimedJobFailedAndKeepsReplacement()
    {
        await using var context = await this.CreateContextAsync();
        var running = Guid.NewGuid();
        var replacement = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(
          running,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1),
          idempotencyKey: "listing:3"));
        var claimed = await ClaimAsync(context.Store);
        await context.Store.EnqueueAsync(CreateRequest(replacement, idempotencyKey: "listing:3"));

        (await context.Store.MarkFailedAsync(new FailJobRequest(running, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeTrue();

        (await GetInspectionReader(context).GetJobAsync(running)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Failed);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(replacement);
    }

    [Fact]
    public async Task IdempotencyKey_ClaimedJobReleaseWithQueuedReplacement_MarksClaimedJobFailedAndKeepsReplacement()
    {
        await using var context = await this.CreateContextAsync();
        var running = Guid.NewGuid();
        var replacement = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(running, idempotencyKey: "listing:3"));
        var claimed = await ClaimAsync(context.Store);
        await context.Store.EnqueueAsync(CreateRequest(replacement, idempotencyKey: "listing:3"));

        (await context.Store.ReleaseJobAsync(new ReleaseJobRequest(running, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        (await GetInspectionReader(context).GetJobAsync(running)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Failed);
        (await ClaimAsync(context.Store)).JobId.ShouldBe(replacement);
    }

    [Fact]
    public async Task DelayedJob_FutureNotBefore_IsNotClaimableUntilDue()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();
        var dueAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);

        await context.Store.EnqueueAsync(CreateRequest(jobId, notBeforeUtc: dueAtUtc));

        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
        await Task.Delay(TimeSpan.FromMilliseconds(350));

        (await ClaimAsync(context.Store)).JobId.ShouldBe(jobId);
    }

    [Fact]
    public async Task TryClaimNext_ConcurrentAttempts_ClaimsJobOnlyOnce()
    {
        await using var context = await this.CreateContextAsync();
        await context.Store.EnqueueAsync(CreateRequest(Guid.NewGuid()));

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
          .Select(index => context.Store.TryClaimNextAsync(CreateClaimRequest($"node-{index}")).AsTask()));

        results.Count(result => result is ClaimJobResult.Claimed).ShouldBe(1);
        results.Count(result => result is ClaimJobResult.NoJobAvailable).ShouldBe(9);
    }

    [Fact]
    public async Task ConcurrencyGroups_SaturatedGroup_SkipsOnlyBlockedJobs()
    {
        await using var context = await this.CreateContextAsync();
        var running = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var eligible = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(running, priority: 100, groupKeys: ["shared"]));
        (await ClaimAsync(context.Store)).JobId.ShouldBe(running);

        await context.Store.EnqueueAsync(CreateRequest(blocked, priority: 100, groupKeys: ["shared"]));
        await context.Store.EnqueueAsync(CreateRequest(eligible, priority: 0));

        (await ClaimAsync(context.Store)).JobId.ShouldBe(eligible);
    }

    [Fact]
    public async Task ConcurrencyLimit_LoweredBelowOccupancy_BlocksFutureClaimsWithoutPreemptingRunningWork()
    {
        await using var context = await this.CreateContextAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var waiting = Guid.NewGuid();

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("critical", 2, ContractClock));
        await context.Store.EnqueueAsync(CreateRequest(first, groupKeys: ["critical"]));
        await context.Store.EnqueueAsync(CreateRequest(second, groupKeys: ["critical"]));
        await context.Store.EnqueueAsync(CreateRequest(waiting, groupKeys: ["critical"]));

        var firstClaim = await ClaimAsync(context.Store);
        firstClaim.JobId.ShouldBe(first);
        var secondClaim = await ClaimAsync(context.Store);
        secondClaim.JobId.ShouldBe(second);

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("critical", 1, ContractClock));
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(first, "node-1", firstClaim.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(second, "node-1", secondClaim.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();
        (await ClaimAsync(context.Store)).JobId.ShouldBe(waiting);
    }

    [Fact]
    public async Task MarkCompleted_CurrentLease_RemovesJobFromClaimableWork()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(jobId));
        var claimed = await ClaimAsync(context.Store);

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task MarkFailed_NoRetryPolicy_FailsTerminally()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(jobId));
        var claimed = await ClaimAsync(context.Store);

        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeTrue();
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task MarkFailed_FixedRetryPolicy_RequeuesAfterBackoff()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(
          jobId,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromMilliseconds(250)));
        var claimed = await ClaimAsync(context.Store);

        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeTrue();
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
        await Task.Delay(TimeSpan.FromMilliseconds(350));

        var retry = await ClaimAsync(context.Store);
        retry.JobId.ShouldBe(jobId);
        retry.AttemptCount.ShouldBe(2);
    }

    [Fact]
    public async Task LeaseOwner_StaleToken_CannotMutateReclaimedJob()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(
          jobId,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromMilliseconds(1)));

        var staleClaim = await ClaimAsync(context.Store, "node-1", TimeSpan.FromMilliseconds(150));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        (await context.Store.RecoverExpiredLeasesAsync(new RecoverExpiredLeasesRequest(DateTimeOffset.UtcNow))).ShouldBe(1);
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        var currentClaim = await ClaimAsync(context.Store, "node-2");

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", staleClaim.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeFalse();
        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", staleClaim.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeFalse();
        (await context.Store.RenewLeaseAsync(new RenewLeaseRequest(jobId, "node-1", staleClaim.LeaseToken, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30)))).ShouldBeFalse();
        (await context.Store.ReleaseJobAsync(new ReleaseJobRequest(jobId, "node-1", staleClaim.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeFalse();
        currentClaim.LeaseToken.ShouldNotBe(staleClaim.LeaseToken);
    }

    [Fact]
    public async Task ReleaseJob_CurrentLease_RequeuesWithoutRetryBudgetConsumption()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(jobId));
        var claimed = await ClaimAsync(context.Store);

        (await context.Store.ReleaseJobAsync(new ReleaseJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        var reclaimed = await ClaimAsync(context.Store);
        reclaimed.JobId.ShouldBe(jobId);
        reclaimed.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_QueuedJob_CancelsBeforeClaim()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(jobId));

        (await context.Store.CancelAsync(new CancelJobRequest(jobId, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.Canceled);

        var detail = await GetInspectionReader(context).GetJobAsync(jobId);
        detail.ShouldNotBeNull().Summary.State.ShouldBe(JobState.Canceled);
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task Cancel_ClaimedJob_RequestsCooperativeCancellation()
    {
        await using var context = await this.CreateContextAsync();
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(jobId));
        var claimedJob = await ClaimAsync(context.Store);

        (await context.Store.CancelAsync(new CancelJobRequest(jobId, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.CancellationRequested);
        var cancellationRequestedAtUtc = await context.Store.GetCancellationRequestedAtAsync(
          new JobCancellationStatusRequest(jobId, "node-1", claimedJob.LeaseToken, DateTimeOffset.UtcNow));
        cancellationRequestedAtUtc.ShouldNotBeNull();

        var detail = await GetInspectionReader(context).GetJobAsync(jobId);
        detail.ShouldNotBeNull().Summary.State.ShouldBe(JobState.Claimed);
        detail.Summary.CancellationRequestedAtUtc.ShouldNotBeNull();

        var events = await ReadEventsAsync(context, jobId);
        events.Count(jobEvent => jobEvent.Kind == JobEventKind.CancelRequested).ShouldBe(1);

        (await context.Store.CancelAsync(new CancelJobRequest(jobId, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.CancellationRequested);
        events = await ReadEventsAsync(context, jobId);
        events.Count(jobEvent => jobEvent.Kind == JobEventKind.CancelRequested).ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_TerminalOrMissingJob_ReturnsNonMutatingResult()
    {
        await using var context = await this.CreateContextAsync();
        var completed = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var canceled = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(completed));
        var completedClaim = await ClaimAsync(context.Store);
        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(completed, "node-1", completedClaim.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        await context.Store.EnqueueAsync(CreateRequest(failed));
        var failedClaim = await ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(failed, "node-1", failedClaim.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeTrue();

        await context.Store.EnqueueAsync(CreateRequest(canceled));
        (await context.Store.CancelAsync(new CancelJobRequest(canceled, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.Canceled);

        (await context.Store.CancelAsync(new CancelJobRequest(completed, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.AlreadyFinished);
        (await context.Store.CancelAsync(new CancelJobRequest(failed, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.AlreadyFinished);
        (await context.Store.CancelAsync(new CancelJobRequest(canceled, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.AlreadyFinished);
        (await context.Store.CancelAsync(new CancelJobRequest(Guid.NewGuid(), DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.NotFound);
    }

    [Fact]
    public async Task CancelQueuedJobs_QueuedState_CancelsAllQueuedAndLeavesOthersUntouched()
    {
        await using var context = await this.CreateContextAsync();
        var claimed = Guid.NewGuid();
        var retryWaiting = Guid.NewGuid();
        var completed = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var alreadyCanceled = Guid.NewGuid();
        var claimable = Guid.NewGuid();
        var delayed = Guid.NewGuid();

        await context.Store.EnqueueAsync(CreateRequest(claimed));
        await ClaimAsync(context.Store);

        await context.Store.EnqueueAsync(CreateRequest(
          retryWaiting,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1)));
        var retryClaim = await ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(retryWaiting, "node-1", retryClaim.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeTrue();

        await context.Store.EnqueueAsync(CreateRequest(completed));
        var completedClaim = await ClaimAsync(context.Store);
        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(completed, "node-1", completedClaim.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        await context.Store.EnqueueAsync(CreateRequest(failed));
        var failedClaim = await ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(failed, "node-1", failedClaim.LeaseToken, DateTimeOffset.UtcNow, CreateFailure()))).ShouldBeTrue();

        await context.Store.EnqueueAsync(CreateRequest(alreadyCanceled));
        (await context.Store.CancelAsync(new CancelJobRequest(alreadyCanceled, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.Canceled);

        await context.Store.EnqueueAsync(CreateRequest(claimable));
        await context.Store.EnqueueAsync(CreateRequest(delayed, notBeforeUtc: DateTimeOffset.UtcNow.AddHours(1)));

        (await context.Store.CancelQueuedJobsAsync(new CancelQueuedJobsRequest(DateTimeOffset.UtcNow))).ShouldBe(3);

        var reader = GetInspectionReader(context);
        (await reader.GetJobAsync(claimable)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Canceled);
        (await reader.GetJobAsync(delayed)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Canceled);
        (await reader.GetJobAsync(retryWaiting)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Canceled);
        (await reader.GetJobAsync(claimed)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Claimed);
        (await reader.GetJobAsync(completed)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Completed);
        (await reader.GetJobAsync(failed)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Failed);
        (await reader.GetJobAsync(alreadyCanceled)).ShouldNotBeNull().Summary.State.ShouldBe(JobState.Canceled);
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task ConcurrencyLimit_SetAndGet_RoundTripsConfiguredLimit()
    {
        await using var context = await this.CreateContextAsync();

        (await context.Store.GetConfiguredConcurrencyLimitAsync("shared")).ShouldBeNull();

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("shared", 3, ContractClock));
        (await context.Store.GetConfiguredConcurrencyLimitAsync("shared")).ShouldBe(3);

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("shared", 5, ContractClock));
        (await context.Store.GetConfiguredConcurrencyLimitAsync("shared")).ShouldBe(5);
    }

    [Fact]
    public async Task ConcurrencyLimit_DefaultOverrideAndClear_UsesEffectivePrecedence()
    {
        await using var context = await this.CreateContextAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var fourth = Guid.NewGuid();

        await context.Store.SetConcurrencyDefaultLimitAsync(new SetConcurrencyDefaultLimitRequest("shared", 2, ContractClock));
        (await context.Store.GetConfiguredConcurrencyLimitAsync("shared")).ShouldBeNull();

        await context.Store.EnqueueAsync(CreateRequest(first, groupKeys: ["shared"]));
        await context.Store.EnqueueAsync(CreateRequest(second, groupKeys: ["shared"]));
        await context.Store.EnqueueAsync(CreateRequest(third, groupKeys: ["shared"]));

        var firstClaim = await ClaimAsync(context.Store);
        firstClaim.JobId.ShouldBe(first);
        var secondClaim = await ClaimAsync(context.Store);
        secondClaim.JobId.ShouldBe(second);
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("shared", 3, ContractClock));
        (await ClaimAsync(context.Store)).JobId.ShouldBe(third);

        await context.Store.EnqueueAsync(CreateRequest(fourth, groupKeys: ["shared"]));
        await context.Store.ClearConcurrencyLimitOverrideAsync(new ClearConcurrencyLimitOverrideRequest("shared", ContractClock));
        (await context.Store.GetConfiguredConcurrencyLimitAsync("shared")).ShouldBeNull();
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(first, "node-1", firstClaim.LeaseToken, ContractClock))).ShouldBeTrue();
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(second, "node-1", secondClaim.LeaseToken, ContractClock))).ShouldBeTrue();
        (await ClaimAsync(context.Store)).JobId.ShouldBe(fourth);
    }

    [Fact]
    public async Task RecurringSchedule_Lifecycle_CreateUpdatePauseResumeListDelete()
    {
        await using var context = await this.CreateContextAsync();

        (await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-b", priority: 0))).ShouldBe(RecurringScheduleUpsertResult.Created);
        (await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a", priority: 0))).ShouldBe(RecurringScheduleUpsertResult.Created);
        (await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a", priority: 0))).ShouldBe(RecurringScheduleUpsertResult.Unchanged);
        (await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a", priority: 5, groupKeys: ["group-a"]))).ShouldBe(RecurringScheduleUpsertResult.Updated);

        var schedule = await context.Store.GetRecurringScheduleAsync("schedule-a");
        schedule.ShouldNotBeNull();
        schedule.Priority.ShouldBe(5);
        schedule.ConcurrencyGroupKeys.ShouldBe(["group-a"]);

        var listed = await context.Store.ListRecurringSchedulesAsync();
        listed.Select(item => item.ScheduleKey).ShouldBe(["schedule-a", "schedule-b"]);

        (await context.Store.PauseRecurringScheduleAsync("schedule-a", ContractClock)).ShouldBeTrue();
        (await context.Store.GetRecurringScheduleAsync("schedule-a")).ShouldNotBeNull().IsPaused.ShouldBeTrue();
        (await context.Store.ResumeRecurringScheduleAsync("schedule-a", DateTimeOffset.UtcNow)).ShouldBeTrue();
        (await context.Store.GetRecurringScheduleAsync("schedule-a")).ShouldNotBeNull().IsPaused.ShouldBeFalse();

        (await context.Store.DeleteRecurringScheduleAsync("schedule-a")).ShouldBeTrue();
        (await context.Store.GetRecurringScheduleAsync("schedule-a")).ShouldBeNull();
        (await context.Store.DeleteRecurringScheduleAsync("schedule-a")).ShouldBeFalse();
    }

    [Fact]
    public async Task RecurringSchedule_UpdateOptions_CanPreserveCronExpression()
    {
        await using var context = await this.CreateContextAsync();

        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a", priority: 1));
        var result = await context.Store.CreateOrUpdateRecurringScheduleAsync(
          CreateSchedule(
            "schedule-a",
            priority: 5,
            updateOptions: new RecurringScheduleUpdateOptions(OverwriteCronExpression: false))
          with
          {
              CronExpression = "*/5 * * * *",
          });

        result.ShouldBe(RecurringScheduleUpsertResult.Updated);
        var schedule = await context.Store.GetRecurringScheduleAsync("schedule-a");
        schedule.ShouldNotBeNull();
        schedule.CronExpression.ShouldBe("* * * * *");
        schedule.Priority.ShouldBe(5);
    }

    [Fact]
    public async Task RecurringSchedule_UpdateOptions_CanPreservePausedState()
    {
        await using var context = await this.CreateContextAsync();

        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a", priority: 1));
        await context.Store.PauseRecurringScheduleAsync("schedule-a", ContractClock);

        var result = await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule(
          "schedule-a",
          priority: 5,
          updateOptions: new RecurringScheduleUpdateOptions(OverwritePausedState: false)));

        result.ShouldBe(RecurringScheduleUpsertResult.Updated);
        var schedule = await context.Store.GetRecurringScheduleAsync("schedule-a");
        schedule.ShouldNotBeNull();
        schedule.IsPaused.ShouldBeTrue();
        schedule.NextFireAtUtc.ShouldBeNull();
        schedule.Priority.ShouldBe(5);
    }

    [Fact]
    public async Task RecurringSchedule_DueOccurrence_MaterializesClaimableJob()
    {
        await using var context = await this.CreateContextAsync();

        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateDueSchedule("schedule-a", priority: 7, groupKeys: ["group-a"]));
        await context.MakeScheduleDueAsync("schedule-a");

        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(DateTimeOffset.UtcNow, null))).ShouldBe(1);

        var claimed = await ClaimAsync(context.Store);
        claimed.SourceScheduleKey.ShouldBe("schedule-a");
        claimed.Priority.ShouldBe(7);
        claimed.ConcurrencyGroupKeys.ShouldBe(["group-a"]);
        claimed.ScheduledFireAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task RecurringOverlap_Skip_DropsOccurrenceWhenEarlierOccurrenceIsNonTerminal()
    {
        await using var context = await this.CreateContextAsync();

        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateDueSchedule("schedule-a", overlapMode: RecurringOverlapMode.Skip));
        await context.MakeScheduleDueAsync("schedule-a");
        var firstMaterializedAtUtc = DateTimeOffset.UtcNow.AddMinutes(2);
        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(firstMaterializedAtUtc, null))).ShouldBe(1);

        await context.MakeScheduleDueAsync("schedule-a");
        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(firstMaterializedAtUtc.AddMinutes(2), null))).ShouldBe(0);

        (await ClaimAsync(context.Store)).SourceScheduleKey.ShouldBe("schedule-a");
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task RecurringOverlap_Allow_MaterializesMultipleNonTerminalOccurrences()
    {
        await using var context = await this.CreateContextAsync();

        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateDueSchedule("schedule-a", overlapMode: RecurringOverlapMode.Allow));
        await context.MakeScheduleDueAsync("schedule-a");
        var firstMaterializedAtUtc = DateTimeOffset.UtcNow.AddMinutes(2);
        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(firstMaterializedAtUtc, null))).ShouldBe(1);

        await context.MakeScheduleDueAsync("schedule-a");
        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(firstMaterializedAtUtc.AddMinutes(2), null))).ShouldBe(1);

        (await ClaimAsync(context.Store)).SourceScheduleKey.ShouldBe("schedule-a");
        (await ClaimAsync(context.Store)).SourceScheduleKey.ShouldBe("schedule-a");
    }

    [Fact]
    public async Task RecurringScheduleTrigger_MissingSchedule_ReturnsNotFound()
    {
        await using var context = await this.CreateContextAsync();

        var result = await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("missing", DefaultRetryPolicy: null));

        result.Status.ShouldBe(RecurringScheduleTriggerStatus.NotFound);
        result.JobId.ShouldBeNull();
        result.EnqueueSequence.ShouldBeNull();
    }

    [Fact]
    public async Task RecurringScheduleTrigger_ExistingSchedule_ClonesTemplateIntoClaimableJob()
    {
        await using var context = await this.CreateContextAsync();
        var retryPolicy = new RetryPolicy(4, RetryBackoffKind.Fixed, TimeSpan.FromSeconds(3));
        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule(
          "schedule-a",
          priority: 7,
          groupKeys: ["group-a"],
          retryPolicy,
          invocationTargetKind: JobInvocationTargetKind.Static,
          methodParameterBindings:
          [
              new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
          ]));

        var result = await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        result.Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        result.JobId.ShouldNotBeNull();
        result.EnqueueSequence.ShouldNotBeNull();
        result.EnqueueSequence.Value.ShouldBeGreaterThan(0);

        var claimed = await ClaimAsync(context.Store);
        claimed.JobId.ShouldBe(result.JobId.Value);
        claimed.EnqueueSequence.ShouldBe(result.EnqueueSequence.Value);
        claimed.SourceScheduleKey.ShouldBe("schedule-a");
        claimed.ScheduledFireAtUtc.ShouldBeNull();
        claimed.Priority.ShouldBe(7);
        claimed.ConcurrencyGroupKeys.ShouldBe(["group-a"]);
        claimed.MaxAttempts.ShouldBe(4);
        claimed.RetryBackoffKind.ShouldBe(RetryBackoffKind.Fixed);
        claimed.RetryBaseDelay.ShouldBe(TimeSpan.FromSeconds(3));
        claimed.ServiceType.ShouldBe(typeof(JobStoreContractService).AssemblyQualifiedName);
        claimed.MethodName.ShouldBe(nameof(JobStoreContractService.RunAsync));
        claimed.MethodParameterTypes.ShouldBe([typeof(CancellationToken).AssemblyQualifiedName!]);
        claimed.InvocationTargetKind.ShouldBe(JobInvocationTargetKind.Static);
        claimed.MethodParameterBindings.ShouldBe([
            new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
        ]);
    }

    [Fact]
    public async Task RecurringScheduleTrigger_DefaultRetryPolicy_AppliesWhenScheduleHasNoRetry()
    {
        await using var context = await this.CreateContextAsync();
        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a"));

        await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest(
          "schedule-a",
          new RetryPolicy(3, RetryBackoffKind.Exponential, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8))));

        var claimed = await ClaimAsync(context.Store);
        claimed.MaxAttempts.ShouldBe(3);
        claimed.RetryBackoffKind.ShouldBe(RetryBackoffKind.Exponential);
        claimed.RetryBaseDelay.ShouldBe(TimeSpan.FromSeconds(2));
        claimed.RetryMaxDelay.ShouldBe(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task RecurringScheduleTrigger_PausedSchedule_EnqueuesJob()
    {
        await using var context = await this.CreateContextAsync();
        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a"));
        await context.Store.PauseRecurringScheduleAsync("schedule-a", ContractClock);

        var result = await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        result.Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        (await ClaimAsync(context.Store)).SourceScheduleKey.ShouldBe("schedule-a");
    }

    [Fact]
    public async Task RecurringScheduleTrigger_SkipOverlap_ReturnsSkippedWhenEarlierOccurrenceIsNonTerminal()
    {
        await using var context = await this.CreateContextAsync();
        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a", overlapMode: RecurringOverlapMode.Skip));

        (await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null)))
          .Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        var skipped = await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        skipped.Status.ShouldBe(RecurringScheduleTriggerStatus.SkippedActiveOccurrence);
        skipped.JobId.ShouldBeNull();
        skipped.EnqueueSequence.ShouldBeNull();
        (await ClaimAsync(context.Store)).SourceScheduleKey.ShouldBe("schedule-a");
        (await context.Store.TryClaimNextAsync(CreateClaimRequest("node-1"))).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task RecurringScheduleTrigger_AllowOverlap_EnqueuesMultipleNonTerminalOccurrences()
    {
        await using var context = await this.CreateContextAsync();
        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a", overlapMode: RecurringOverlapMode.Allow));

        (await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null)))
          .Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        (await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null)))
          .Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);

        (await ClaimAsync(context.Store)).SourceScheduleKey.ShouldBe("schedule-a");
        (await ClaimAsync(context.Store)).SourceScheduleKey.ShouldBe("schedule-a");
    }

    [Fact]
    public async Task RecurringScheduleTrigger_ExistingSchedule_DoesNotAdvanceScheduleOrChangePausedState()
    {
        await using var context = await this.CreateContextAsync();
        await context.Store.CreateOrUpdateRecurringScheduleAsync(CreateSchedule("schedule-a"));
        var before = await context.Store.GetRecurringScheduleAsync("schedule-a");
        before.ShouldNotBeNull();

        await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        var after = await context.Store.GetRecurringScheduleAsync("schedule-a");
        after.ShouldNotBeNull();
        after.NextFireAtUtc.ShouldBe(before.NextFireAtUtc);
        after.IsPaused.ShouldBe(before.IsPaused);
    }

    protected static async ValueTask<ClaimedJob> ClaimAsync(
        IJobStore store,
        string nodeId = "node-1",
        TimeSpan? leaseDuration = null)
    {
        return (await store.TryClaimNextAsync(CreateClaimRequest(nodeId, leaseDuration: leaseDuration)))
          .ShouldBeOfType<ClaimJobResult.Claimed>()
          .Job;
    }

    protected static ClaimJobRequest CreateClaimRequest(
        string nodeId,
        DateTimeOffset? claimedAtUtc = null,
        TimeSpan? leaseDuration = null)
    {
        var claimedAt = claimedAtUtc ?? DateTimeOffset.UtcNow;
        return new ClaimJobRequest(nodeId, claimedAt, claimedAt.Add(leaseDuration ?? TimeSpan.FromSeconds(30)));
    }

    protected static EnqueueJobRequest CreateRequest(
        Guid jobId,
        int priority = 0,
        DateTimeOffset? notBeforeUtc = null,
        int maxAttempts = 1,
        RetryBackoffKind? retryBackoffKind = null,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? retryMaxDelay = null,
        IReadOnlyList<string>? groupKeys = null,
        string? idempotencyKey = null,
        IReadOnlyList<Guid>? prerequisiteJobIds = null)
      => new(
        jobId,
        priority,
        typeof(JobStoreContractService).AssemblyQualifiedName!,
        nameof(JobStoreContractService.RunAsync),
        [typeof(CancellationToken).AssemblyQualifiedName!],
        EmptyPayload(),
        groupKeys ?? [],
        DateTimeOffset.UtcNow,
        notBeforeUtc,
        maxAttempts,
        retryBackoffKind,
        retryBaseDelay,
        retryMaxDelay,
        IdempotencyKey: idempotencyKey,
        PrerequisiteJobIds: prerequisiteJobIds);

    protected static UpsertRecurringScheduleRequest CreateSchedule(
        string scheduleKey,
        int priority = 0,
        IReadOnlyList<string>? groupKeys = null,
        RetryPolicy? retryPolicy = null,
        RecurringOverlapMode overlapMode = RecurringOverlapMode.Skip,
        DateTimeOffset? upsertedAtUtc = null,
        JobInvocationTargetKind invocationTargetKind = JobInvocationTargetKind.Instance,
        IReadOnlyList<JobMethodParameterBinding>? methodParameterBindings = null,
        RecurringScheduleUpdateOptions? updateOptions = null)
      => new(
        scheduleKey,
        "* * * * *",
        typeof(JobStoreContractService).AssemblyQualifiedName!,
        nameof(JobStoreContractService.RunAsync),
        [typeof(CancellationToken).AssemblyQualifiedName!],
        EmptyPayload(),
        priority,
        groupKeys ?? [],
        retryPolicy,
        overlapMode,
        upsertedAtUtc ?? DateTimeOffset.UtcNow,
        InvocationTargetKind: invocationTargetKind,
        MethodParameterBindings: methodParameterBindings,
        UpdateOptions: updateOptions);

    protected static UpsertRecurringScheduleRequest CreateDueSchedule(
        string scheduleKey,
        int priority = 0,
        IReadOnlyList<string>? groupKeys = null,
        RetryPolicy? retryPolicy = null,
        RecurringOverlapMode overlapMode = RecurringOverlapMode.Skip)
      => CreateSchedule(
        scheduleKey,
        priority,
        groupKeys,
        retryPolicy,
        overlapMode,
        DateTimeOffset.UtcNow.AddMinutes(-2));

    protected static JobFailureInfo CreateFailure()
      => new("TestException", "failed", null);

    protected static SerializedJobPayload EmptyPayload()
      => new(SystemTextJsonJobPayloadSerializer.JsonContentType, "[]"u8.ToArray());

    private static IJobInspectionReader GetInspectionReader(JobStoreContractContext context)
      => context.InspectionReader
        ?? throw new InvalidOperationException("Job store contract contexts must provide job inspection.");

    private static async ValueTask<IReadOnlyList<JobEvent>> ReadEventsAsync(
        JobStoreContractContext context,
        Guid jobId)
    {
        var events = new List<JobEvent>();
        await foreach (var jobEvent in GetInspectionReader(context).ReadEventsAsync(jobId))
        {
            events.Add(jobEvent);
        }

        return events;
    }

    private sealed class JobStoreContractService
    {
        public Task RunAsync(CancellationToken cancellationToken)
          => Task.CompletedTask;
    }
}
