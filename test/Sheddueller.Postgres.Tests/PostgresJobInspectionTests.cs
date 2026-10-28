namespace Sheddueller.Postgres.Tests;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Sheddueller.Inspection.ConcurrencyGroups;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class PostgresJobInspectionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task JobDetail_SingleConnectionPool_ReadsWholeDetailOnOneConnection()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var running = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var claimable = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(running, priority: 100, groupKeys: ["shared"]));
        await PostgresTestData.ClaimAsync(context.Store);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          blocked,
          priority: 100,
          groupKeys: ["shared"],
          tags: [new JobTag("tenant", "acme"), new JobTag("domain", "payments")]));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(claimable, priority: 50));
        await ((IJobEventSink)context.Store).AppendAsync(new AppendJobEventRequest(
          blocked,
          JobEventKind.Progress,
          AttemptNumber: 0,
          Message: "quarter",
          ProgressPercent: 25));

        // With one pooled connection, a detail read that opened a second connection would exhaust the pool.
        await using var singleConnectionDataSource = NpgsqlDataSource.Create(
          new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { MaxPoolSize = 1, Timeout = 5 }.ConnectionString);
        await using var provider = PostgresTestContext.CreateProvider(singleConnectionDataSource, context.SchemaName);
        var reader = provider.GetRequiredService<IJobInspectionReader>();

        var blockedDetail = (await reader.GetJobAsync(blocked)).ShouldNotBeNull();
        blockedDetail.Summary.QueuePosition.ShouldNotBeNull().Kind.ShouldBe(JobQueuePositionKind.BlockedByConcurrency);
        blockedDetail.Summary.Tags.ShouldBe([new JobTag("tenant", "acme"), new JobTag("domain", "payments")]);
        blockedDetail.Summary.ConcurrencyGroupKeys.ShouldBe(["shared"]);
        blockedDetail.Summary.LatestProgress.ShouldNotBeNull().Percent.ShouldBe(25);
        blockedDetail.Invocation.ShouldNotBeNull().MethodName.ShouldBe(PostgresTestData.MethodName);

        var claimableDetail = (await reader.GetJobAsync(claimable)).ShouldNotBeNull();
        claimableDetail.Summary.QueuePosition.ShouldNotBeNull().Kind.ShouldBe(JobQueuePositionKind.Claimable);
        claimableDetail.Summary.QueuePosition.Position.ShouldBe(1);

        var runningDetail = (await reader.GetJobAsync(running)).ShouldNotBeNull();
        runningDetail.Summary.QueuePosition.ShouldNotBeNull().Kind.ShouldBe(JobQueuePositionKind.Claimed);
        runningDetail.ClaimedByNodeId.ShouldBe("node-1");

        (await reader.GetQueuePositionAsync(blocked)).Kind.ShouldBe(JobQueuePositionKind.BlockedByConcurrency);
        (await reader.GetJobAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task QueuePosition_RateLimitedGroup_ReportsTheJobTheClaimSkips()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var reader = (IJobInspectionReader)context.Store;
        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("api", 10, DateTimeOffset.UtcNow));
        await context.Store.SetConcurrencyDefaultRateLimitAsync(new SetConcurrencyDefaultRateLimitRequest(
          "api",
          new ConcurrencyGroupRateLimit(1, TimeSpan.FromHours(1)),
          DateTimeOffset.UtcNow));
        var running = Guid.NewGuid();
        var rateBlocked = Guid.NewGuid();
        var claimable = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(running, priority: 100, groupKeys: ["api"]));
        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(running);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(rateBlocked, priority: 100, groupKeys: ["api"]));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(claimable, priority: 0));

        var blockedPosition = await reader.GetQueuePositionAsync(rateBlocked);
        var claimablePosition = await reader.GetQueuePositionAsync(claimable);
        var page = await reader.SearchJobsAsync(new JobInspectionQuery(States: [JobState.Queued]));
        var overview = await reader.GetOverviewAsync();
        var group = await ((IConcurrencyGroupInspectionReader)context.Store).GetConcurrencyGroupAsync("api");

        blockedPosition.Kind.ShouldBe(JobQueuePositionKind.BlockedByConcurrency);
        blockedPosition.Position.ShouldBeNull();
        claimablePosition.Kind.ShouldBe(JobQueuePositionKind.Claimable);
        claimablePosition.Position.ShouldBe(1);
        page.Jobs.Select(job => job.JobId).ShouldBe([claimable, rateBlocked]);
        page.Jobs.Select(job => job.QueuePosition?.Position).ShouldBe([1L, null]);
        overview.QueuedJobs.Select(job => job.JobId).ShouldBe([claimable]);
        group.ShouldNotBeNull().RateBlockedJobIds.ShouldBe([rateBlocked]);
        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(claimable);
    }

    [Fact]
    public async Task Overview_UnfinishedPrerequisite_ListsOnlyClaimableQueuedJobs()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var reader = (IJobInspectionReader)context.Store;
        var prerequisite = Guid.NewGuid();
        var dependent = Guid.NewGuid();
        await context.Store.EnqueueManyAsync([
          PostgresTestData.CreateRequest(prerequisite, priority: 0),
          PostgresTestData.CreateRequest(dependent, priority: 100) with { PrerequisiteJobIds = [prerequisite] },
        ]);

        var overview = await reader.GetOverviewAsync();

        overview.QueuedJobs.Select(job => job.JobId).ShouldBe([prerequisite]);
        overview.QueuedJobs[0].QueuePosition.ShouldNotBeNull().Position.ShouldBe(1);
        (await reader.GetQueuePositionAsync(dependent)).Kind.ShouldBe(JobQueuePositionKind.WaitingForDependencies);
    }

    [Fact]
    public async Task SearchJobs_UnfinishedPrerequisite_SortsDependentAfterClaimableJobs()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var reader = (IJobInspectionReader)context.Store;
        var prerequisite = Guid.NewGuid();
        var dependent = Guid.NewGuid();
        await context.Store.EnqueueManyAsync([
          PostgresTestData.CreateRequest(prerequisite, priority: 0),
          PostgresTestData.CreateRequest(dependent, priority: 100) with { PrerequisiteJobIds = [prerequisite] },
        ]);

        var page = await reader.SearchJobsAsync(new JobInspectionQuery(States: [JobState.Queued]));

        page.Jobs.Select(job => job.JobId).ShouldBe([prerequisite, dependent]);
        page.Jobs.Select(job => job.QueuePosition?.Kind).ShouldBe([
            JobQueuePositionKind.Claimable,
            JobQueuePositionKind.WaitingForDependencies,
        ]);
    }

    [Fact]
    public async Task SearchJobs_CanceledAfterFailedAttempt_SortsByCancellationTime()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var reader = (IJobInspectionReader)context.Store;
        var now = DateTimeOffset.UtcNow;
        var canceledAfterRetry = Guid.NewGuid();
        var completedYesterday = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          canceledAfterRetry,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1)));
        var attempt = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(
          canceledAfterRetry,
          "node-1",
          attempt.LeaseToken,
          now.AddDays(-10),
          PostgresTestData.CreateFailure()))).ShouldBeTrue();
        (await context.Store.CancelAsync(new CancelJobRequest(canceledAfterRetry, now))).ShouldBe(JobCancellationResult.Canceled);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(completedYesterday));
        var completedClaim = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(
          completedYesterday,
          "node-1",
          completedClaim.LeaseToken,
          now.AddDays(-1)))).ShouldBeTrue();

        var page = await reader.SearchJobsAsync(new JobInspectionQuery(States: [JobState.Completed, JobState.Canceled]));

        page.Jobs.Select(job => job.JobId).ShouldBe([canceledAfterRetry, completedYesterday]);
    }
}
