namespace Sheddueller.Postgres.Tests.Operations;

using Microsoft.Extensions.DependencyInjection;

using Sheddueller.Inspection.Nodes;
using Sheddueller.Storage;

using Shouldly;

public sealed class WorkerNodeRetentionTests(PostgresFixture fixture)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task CleanupWorkerNodes_HeartbeatOlderThanRetention_RemovesOnlyThoseNodes()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await RecordHeartbeatAsync(context, "node-gone");
        await RecordHeartbeatAsync(context, "node-dead");
        await RecordHeartbeatAsync(context, "node-live");
        await MoveLastHeartbeatBackAsync(context, "node-gone", TimeSpan.FromMinutes(31));
        await MoveLastHeartbeatBackAsync(context, "node-dead", TimeSpan.FromMinutes(29));

        var removed = await RetentionStore(context).CleanupWorkerNodesAsync(Retention);

        removed.ShouldBe(1);
        var nodes = await context.NodeReader.SearchNodesAsync(new NodeInspectionQuery());
        nodes.Nodes.Select(node => node.NodeId).ShouldBe(["node-dead", "node-live"], ignoreOrder: true);
        (await context.NodeReader.GetNodeAsync("node-gone")).ShouldBeNull();
    }

    [Fact]
    public async Task CleanupWorkerNodes_RemovedNodeHeartbeatsAgain_RegistersAgain()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await RecordHeartbeatAsync(context, "node-paused");
        await MoveLastHeartbeatBackAsync(context, "node-paused", TimeSpan.FromHours(1));
        (await RetentionStore(context).CleanupWorkerNodesAsync(Retention)).ShouldBe(1);

        await RecordHeartbeatAsync(context, "node-paused");

        var node = await context.NodeReader.GetNodeAsync("node-paused");
        node.ShouldNotBeNull().Summary.State.ShouldBe(NodeHealthState.Active);
    }

    [Fact]
    public async Task CleanupWorkerNodes_NothingOld_RemovesNothing()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await RecordHeartbeatAsync(context, "node-live");

        (await RetentionStore(context).CleanupWorkerNodesAsync(Retention)).ShouldBe(0);
    }

    private static IWorkerNodeRetentionStore RetentionStore(PostgresTestContext context)
      => context.Provider.GetRequiredService<IWorkerNodeRetentionStore>();

    private static ValueTask RecordHeartbeatAsync(PostgresTestContext context, string nodeId)
      => context.Store.RecordWorkerNodeHeartbeatAsync(new WorkerNodeHeartbeatRequest(
        nodeId,
        DateTimeOffset.UtcNow,
        MaxConcurrentExecutionsPerNode: 2,
        CurrentExecutionCount: 0));

    private static ValueTask MoveLastHeartbeatBackAsync(PostgresTestContext context, string nodeId, TimeSpan age)
      => context.ExecuteAsync(
        $"""
        update {context.Table("worker_nodes")}
        set last_heartbeat_at_utc = transaction_timestamp() - @age
        where node_id = @node_id;
        """,
        command =>
        {
            command.Parameters.AddWithValue("node_id", nodeId);
            command.Parameters.AddWithValue("age", age);
        });
}
