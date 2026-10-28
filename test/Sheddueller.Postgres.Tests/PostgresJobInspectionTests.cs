namespace Sheddueller.Postgres.Tests;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class PostgresJobInspectionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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
}
