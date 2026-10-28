namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using NpgsqlTypes;

internal static class PostgresJobGroups
{
    /// <summary>
    /// A <c>text[]</c> expression of the job's concurrency group keys in key order, for the select list of a query
    /// over the jobs table aliased <paramref name="job" />. A job's groups never change after it is enqueued.
    /// </summary>
    public static string GroupKeysSql(PostgresNames names, string job)
      => $"""
         array(
             select job_group.group_key
             from {names.JobConcurrencyGroups} job_group
             where job_group.job_id = {job}.job_id
             order by job_group.group_key asc)
         """;

    public static async ValueTask EnsureGroupRowsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> groupKeys,
        CancellationToken cancellationToken)
    {
        if (groupKeys.Count == 0)
        {
            return;
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.ConcurrencyGroups} (group_key, configured_limit, in_use_count, updated_at_utc)
          select group_key, null, 0, transaction_timestamp()
          from unnest(@group_keys::text[]) as concurrency_group(group_key)
          on conflict (group_key) do nothing;
          """,
          command => command.Parameters.Add("group_keys", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = groupKeys.ToArray(),
          cancellationToken)
          .ConfigureAwait(false);
    }

    public static async ValueTask<bool> TryReserveGroupsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> groupKeys,
        CancellationToken cancellationToken)
    {
        if (groupKeys.Count == 0)
        {
            return true;
        }

        await using var command = PostgresCommands.Create(
          connection,
          transaction,
          $"""
          select
              concurrency_group.group_key,
              {PostgresSqlFragments.GroupIsSaturated("concurrency_group")}
                  or {PostgresSqlFragments.GroupIsRateLimited("concurrency_group", PostgresSqlFragments.ClockNow)} as is_blocked
          from {context.Names.ConcurrencyGroups} concurrency_group
          where concurrency_group.group_key = any(@group_keys)
          order by concurrency_group.group_key asc
          for update;
          """);
        command.Parameters.AddWithValue("group_keys", groupKeys.ToArray());

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var lockCount = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lockCount++;
                if (reader.GetBoolean(1))
                {
                    return false;
                }
            }

            if (lockCount != groupKeys.Count)
            {
                return false;
            }
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.ConcurrencyGroups}
          set in_use_count = in_use_count + 1,
              rate_theoretical_arrival_at_utc = case
                  when effective_rate_permit_count is null then null
                  else greatest(
                      coalesce(rate_theoretical_arrival_at_utc, clock_timestamp()),
                      clock_timestamp())
                      + (effective_rate_period / effective_rate_permit_count)
              end,
              updated_at_utc = transaction_timestamp()
          where group_key = any(@group_keys);
          """,
          updateCommand => updateCommand.Parameters.AddWithValue("group_keys", groupKeys.ToArray()),
          cancellationToken)
          .ConfigureAwait(false);

        return true;
    }

    public static async ValueTask DecrementGroupsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> groupKeys,
        CancellationToken cancellationToken)
    {
        if (groupKeys.Count == 0)
        {
            return;
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          select group_key
          from {context.Names.ConcurrencyGroups}
          where group_key = any(@group_keys)
          order by group_key asc
          for update;
          """,
          command => command.Parameters.AddWithValue("group_keys", groupKeys.ToArray()),
          cancellationToken)
          .ConfigureAwait(false);

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.ConcurrencyGroups}
          set in_use_count = in_use_count - 1,
              updated_at_utc = transaction_timestamp()
          where group_key = any(@group_keys);
          """,
          command => command.Parameters.AddWithValue("group_keys", groupKeys.ToArray()),
          cancellationToken)
          .ConfigureAwait(false);
    }

    public static async ValueTask InsertJobGroupsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        IReadOnlyList<string> groupKeys,
        CancellationToken cancellationToken)
    {
        var distinctGroupKeys = groupKeys.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctGroupKeys.Length == 0)
        {
            return;
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.JobConcurrencyGroups} (job_id, group_key)
          select @job_id, group_key
          from unnest(@group_keys::text[]) as job_group(group_key)
          on conflict (job_id, group_key) do nothing;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_id", jobId);
              command.Parameters.Add("group_keys", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = distinctGroupKeys;
          },
          cancellationToken)
          .ConfigureAwait(false);
    }
}
