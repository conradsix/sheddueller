namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

internal static class PostgresJobGroups
{
    public static async ValueTask<IReadOnlyList<string>> ReadJobGroupKeysAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select group_key
          from {context.Names.JobConcurrencyGroups}
          where job_id = @job_id
          order by group_key asc;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        var groupKeys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groupKeys.Add(reader.GetString(0));
        }

        return groupKeys;
    }

    public static async ValueTask EnsureGroupRowsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> groupKeys,
        CancellationToken cancellationToken)
    {
        foreach (var groupKey in groupKeys)
        {
            await PostgresOperationContext.ExecuteCountAsync(
              connection,
              transaction,
              $"""
              insert into {context.Names.ConcurrencyGroups} (group_key, configured_limit, in_use_count, updated_at_utc)
              values (@group_key, null, 0, transaction_timestamp())
              on conflict (group_key) do nothing;
              """,
              command => command.Parameters.AddWithValue("group_key", groupKey),
              cancellationToken)
              .ConfigureAwait(false);
        }
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

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select
              concurrency_group.group_key,
              {PostgresSqlFragments.GroupIsSaturated("concurrency_group")}
                  or {PostgresSqlFragments.GroupIsRateLimited("concurrency_group", PostgresSqlFragments.ClockNow)} as is_blocked
          from {context.Names.ConcurrencyGroups} concurrency_group
          where concurrency_group.group_key = any(@group_keys)
          order by concurrency_group.group_key asc
          for update;
          """;
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

    public static async ValueTask ReplaceJobGroupsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        IReadOnlyList<string> groupKeys,
        CancellationToken cancellationToken)
    {
        foreach (var groupKey in groupKeys.Distinct(StringComparer.Ordinal))
        {
            await PostgresOperationContext.ExecuteCountAsync(
              connection,
              transaction,
              $"""
              insert into {context.Names.JobConcurrencyGroups} (job_id, group_key)
              values (@job_id, @group_key)
              on conflict (job_id, group_key) do nothing;
              """,
              command =>
              {
                  command.Parameters.AddWithValue("job_id", jobId);
                  command.Parameters.AddWithValue("group_key", groupKey);
              },
              cancellationToken)
              .ConfigureAwait(false);
        }
    }
}
