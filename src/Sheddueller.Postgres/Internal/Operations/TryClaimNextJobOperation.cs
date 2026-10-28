namespace Sheddueller.Postgres.Internal.Operations;

using System.Data;

using Npgsql;

using Sheddueller.Storage;

internal static class TryClaimNextJobOperation
{
    private const int ClaimCandidateLimit = 8;

    public static async ValueTask<ClaimJobResult> ExecuteAsync(
        PostgresOperationContext context,
        ClaimJobRequest request,
        CancellationToken cancellationToken)
    {
        var leaseDuration = request.LeaseExpiresAtUtc - request.ClaimedAtUtc;
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Lease expiry must be after the claimed timestamp.", nameof(request));
        }

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        var candidates = await ReadClaimCandidatesAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var jobId in candidates)
        {
            var groupKeys = await PostgresJobGroups.ReadJobGroupKeysAsync(context, connection, transaction, jobId, cancellationToken)
              .ConfigureAwait(false);
            await PostgresJobGroups.EnsureGroupRowsAsync(context, connection, transaction, groupKeys, cancellationToken).ConfigureAwait(false);

            if (!await PostgresJobGroups.TryReserveGroupsAsync(context, connection, transaction, groupKeys, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var leaseToken = Guid.NewGuid();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
              $"""
              update {context.Names.Jobs}
              set state = 'Claimed',
                  attempt_count = attempt_count + 1,
                  claimed_by_node_id = @node_id,
                  claimed_at_utc = transaction_timestamp(),
                  lease_token = @lease_token,
                  lease_expires_at_utc = transaction_timestamp() + @lease_duration,
                  last_heartbeat_at_utc = null
              where job_id = @job_id
                and state = 'Queued'
              returning
                  job_id,
                  enqueue_sequence,
                  priority,
                  service_type,
                  method_name,
                  method_parameter_types,
                  invocation_target_kind,
                  method_parameter_bindings,
                  serialized_arguments_content_type,
                  serialized_arguments,
                  attempt_count,
                  max_attempts,
                  lease_token,
                  lease_expires_at_utc,
                  retry_backoff_kind,
                  retry_base_delay_ms,
                  retry_max_delay_ms,
                  source_schedule_key,
                  scheduled_fire_at_utc;
              """;
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("node_id", request.NodeId);
            command.Parameters.AddWithValue("lease_token", leaseToken);
            command.Parameters.AddWithValue("lease_duration", leaseDuration);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var claimed = PostgresReaders.ReadClaimedJob(reader, groupKeys);
            await reader.DisposeAsync().ConfigureAwait(false);
            await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
              context,
              connection,
              transaction,
              new AppendJobEventRequest(claimed.JobId, JobEventKind.AttemptStarted, claimed.AttemptCount, Message: "Attempt started"),
              cancellationToken)
              .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new ClaimJobResult.Claimed(claimed);
        }

        var nextClaimAtUtc = await ReadNextRateLimitedClaimAtAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ClaimJobResult.NoJobAvailable(nextClaimAtUtc);
    }

    private static async ValueTask<IReadOnlyList<Guid>> ReadClaimCandidatesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select job.job_id
          from {context.Names.Jobs} job
          where {PostgresSqlFragments.JobIsClaimable(context.Names, "job", PostgresSqlFragments.ClockNow)}
          order by {PostgresSqlFragments.ClaimOrder("job")}
          for update of job skip locked
          limit @candidate_limit;
          """;
        command.Parameters.AddWithValue("candidate_limit", ClaimCandidateLimit);

        var jobIds = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobIds.Add(reader.GetGuid(0));
        }

        return jobIds;
    }

    private static async ValueTask<DateTimeOffset?> ReadNextRateLimitedClaimAtAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select min(rate_blocked.next_claim_at_utc)
          from (
              select job.job_id, max(concurrency_group.rate_theoretical_arrival_at_utc) as next_claim_at_utc
              from {context.Names.Jobs} job
              join {context.Names.JobConcurrencyGroups} job_group on job_group.job_id = job.job_id
              join {context.Names.ConcurrencyGroups} concurrency_group on concurrency_group.group_key = job_group.group_key
              where {PostgresSqlFragments.JobIsDue("job", PostgresSqlFragments.TransactionNow)}
                and not {PostgresSqlFragments.HasUnfinishedPrerequisites(context.Names, "job")}
                and {PostgresSqlFragments.GroupIsRateLimited("concurrency_group", PostgresSqlFragments.ClockNow)}
              group by job.job_id
          ) rate_blocked;
          """;

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : PostgresConversion.ToDateTimeOffset(value);
    }
}
