namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using Sheddueller.Inspection.Nodes;

internal static class PostgresNodeInspectionOperation
{
    public static async ValueTask<NodeInspectionPage> SearchAsync(
        PostgresOperationContext context,
        NodeInspectionQuery query,
        TimeSpan staleThreshold,
        TimeSpan deadThreshold,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var totalCount = await ReadTotalCountAsync(context, connection, query, staleThreshold, deadThreshold, cancellationToken).ConfigureAwait(false);
        var nodes = await ReadNodeSummaryPageAsync(context, connection, query, staleThreshold, deadThreshold, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var pageItems = nodes.Take(query.PageSize).ToArray();

        return new NodeInspectionPage(
          pageItems,
          nodes.Count > query.PageSize ? pageItems[^1].NodeId : null,
          totalCount);
    }

    public static async ValueTask<NodeInspectionDetail?> GetAsync(
        PostgresOperationContext context,
        string nodeId,
        TimeSpan staleThreshold,
        TimeSpan deadThreshold,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var summary = await ReadNodeSummaryAsync(context, connection, nodeId, staleThreshold, deadThreshold, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var detail = new NodeInspectionDetail(
          summary,
          await ReadClaimedJobIdsAsync(context, connection, nodeId, cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return detail;
    }

    private static async ValueTask<long> ReadTotalCountAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NodeInspectionQuery query,
        TimeSpan staleThreshold,
        TimeSpan deadThreshold,
        CancellationToken cancellationToken)
    {
        var conditions = new List<string>();
        ConfigureFilters(conditions, query);
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select count(*)
          from {context.Names.WorkerNodes} node
          {PostgresSql.WhereClause(conditions)};
          """);
        if (query.State is not null)
        {
            command.Parameters.AddWithValue("stale_threshold", staleThreshold);
            command.Parameters.AddWithValue("dead_threshold", deadThreshold);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<IReadOnlyList<NodeInspectionSummary>> ReadNodeSummaryPageAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NodeInspectionQuery query,
        TimeSpan staleThreshold,
        TimeSpan deadThreshold,
        CancellationToken cancellationToken)
    {
        var conditions = new List<string>();
        ConfigureFilters(conditions, query);
        if (query.ContinuationToken is not null)
        {
            conditions.Add("node.node_id > @after_node_id");
        }

        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          with page_nodes as (
              select
                  node.node_id,
                  {CreateNodeStateSql("node")} as state,
                  node.first_seen_at_utc,
                  node.last_heartbeat_at_utc,
                  node.max_concurrent_executions_per_node,
                  node.current_execution_count
              from {context.Names.WorkerNodes} node
              {PostgresSql.WhereClause(conditions)}
              order by node.node_id asc
              limit @limit
          ),
          claimed_counts as (
              select
                  job.claimed_by_node_id as node_id,
                  count(*) as claimed_count
              from {context.Names.Jobs} job
              join page_nodes node on node.node_id = job.claimed_by_node_id
              where job.state = 'Claimed'
                and job.claimed_by_node_id is not null
              group by job.claimed_by_node_id
          )
          select
              page_nodes.node_id,
              page_nodes.state,
              page_nodes.first_seen_at_utc,
              page_nodes.last_heartbeat_at_utc,
              coalesce(claimed_counts.claimed_count, 0),
              page_nodes.max_concurrent_executions_per_node,
              page_nodes.current_execution_count
          from page_nodes
          left join claimed_counts on claimed_counts.node_id = page_nodes.node_id
          order by page_nodes.node_id asc;
          """);
        if (query.ContinuationToken is not null)
        {
            command.Parameters.AddWithValue("after_node_id", query.ContinuationToken);
        }

        command.Parameters.AddWithValue("stale_threshold", staleThreshold);
        command.Parameters.AddWithValue("dead_threshold", deadThreshold);
        command.Parameters.AddWithValue("limit", query.PageSize + 1);

        return await ReadNodeSummariesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<NodeInspectionSummary?> ReadNodeSummaryAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string nodeId,
        TimeSpan staleThreshold,
        TimeSpan deadThreshold,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          with selected_node as (
              select
                  node.node_id,
                  {CreateNodeStateSql("node")} as state,
                  node.first_seen_at_utc,
                  node.last_heartbeat_at_utc,
                  node.max_concurrent_executions_per_node,
                  node.current_execution_count
              from {context.Names.WorkerNodes} node
              where node.node_id = @node_id
          ),
          claimed_counts as (
              select count(*) as claimed_count
              from {context.Names.Jobs} job
              join selected_node node on node.node_id = job.claimed_by_node_id
              where job.state = 'Claimed'
                and job.claimed_by_node_id is not null
          )
          select
              selected_node.node_id,
              selected_node.state,
              selected_node.first_seen_at_utc,
              selected_node.last_heartbeat_at_utc,
              claimed_counts.claimed_count,
              selected_node.max_concurrent_executions_per_node,
              selected_node.current_execution_count
          from selected_node
          cross join claimed_counts;
          """);
        command.Parameters.AddWithValue("node_id", nodeId);
        command.Parameters.AddWithValue("stale_threshold", staleThreshold);
        command.Parameters.AddWithValue("dead_threshold", deadThreshold);

        var rows = await ReadNodeSummariesAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async ValueTask<IReadOnlyList<NodeInspectionSummary>> ReadNodeSummariesAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var nodes = new List<NodeInspectionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(new NodeInspectionSummary(
              reader.GetString(0),
              Enum.Parse<NodeHealthState>(reader.GetString(1)),
              PostgresConversion.ToDateTimeOffset(reader.GetValue(2)),
              PostgresConversion.ToDateTimeOffset(reader.GetValue(3)),
              Convert.ToInt32(reader.GetInt64(4), CultureInfo.InvariantCulture),
              reader.GetInt32(5),
              reader.GetInt32(6)));
        }

        return nodes;
    }

    private static async ValueTask<IReadOnlyList<Guid>> ReadClaimedJobIdsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select job_id
          from {context.Names.Jobs}
          where state = 'Claimed'
            and claimed_by_node_id = @node_id
          order by enqueue_sequence asc;
          """);
        command.Parameters.AddWithValue("node_id", nodeId);

        return await PostgresSql.ReadGuidsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static void ConfigureFilters(
        List<string> conditions,
        NodeInspectionQuery query)
    {
        if (query.State is not { } state)
        {
            return;
        }

        conditions.Add(state switch
        {
            NodeHealthState.Active => "transaction_timestamp() - node.last_heartbeat_at_utc < @stale_threshold",
            NodeHealthState.Stale => "transaction_timestamp() - node.last_heartbeat_at_utc >= @stale_threshold and transaction_timestamp() - node.last_heartbeat_at_utc < @dead_threshold",
            NodeHealthState.Dead => "transaction_timestamp() - node.last_heartbeat_at_utc >= @dead_threshold",
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.State, "Node health state is invalid."),
        });
    }

    private static string CreateNodeStateSql(string nodeAlias)
      => $"""
         case
             when transaction_timestamp() - {nodeAlias}.last_heartbeat_at_utc >= @dead_threshold then 'Dead'
             when transaction_timestamp() - {nodeAlias}.last_heartbeat_at_utc >= @stale_threshold then 'Stale'
             else 'Active'
         end
         """;

    private static void ValidateQuery(NodeInspectionQuery query)
    {
        if (query.PageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.PageSize, "Node inspection query page size must be positive.");
        }

        if (query.ContinuationToken is { Length: 0 })
        {
            throw new ArgumentException("Node inspection continuation token is invalid.", nameof(query));
        }
    }
}
