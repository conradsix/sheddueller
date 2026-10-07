namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

internal static class PostgresJobQueuePositions
{
    public static async ValueTask<JobQueuePosition> GetQueuePositionAsync(
        PostgresOperationContext context,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await PostgresJobInspectionRows.ReadJobRowAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return new JobQueuePosition(jobId, JobQueuePositionKind.NotFound, Position: null, "Job was not found.");
        }

        if (row.State == JobState.Canceled)
        {
            return new JobQueuePosition(jobId, JobQueuePositionKind.Canceled, Position: null, "Job was canceled.");
        }

        if (row.State is JobState.Completed or JobState.Failed)
        {
            return new JobQueuePosition(jobId, JobQueuePositionKind.Terminal, Position: null, "Job is terminal.");
        }

        if (row.State == JobState.Claimed)
        {
            return new JobQueuePosition(jobId, JobQueuePositionKind.Claimed, Position: null, "Job is currently claimed.");
        }

        if (await HasUnfinishedPrerequisitesAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false))
        {
            return new JobQueuePosition(
              jobId,
              JobQueuePositionKind.WaitingForDependencies,
              Position: null,
              "Job is waiting for prerequisite jobs to become terminal.");
        }

        var now = await PostgresJobInspectionRows.ReadCurrentTimestampAsync(connection, cancellationToken).ConfigureAwait(false);
        if (row.NotBeforeUtc is { } notBeforeUtc && notBeforeUtc > now)
        {
            return row.FailedAtUtc is null
              ? new JobQueuePosition(jobId, JobQueuePositionKind.Delayed, Position: null, $"Job is delayed until {notBeforeUtc:O}.")
              : new JobQueuePosition(jobId, JobQueuePositionKind.RetryWaiting, Position: null, $"Job is waiting to retry until {notBeforeUtc:O}.");
        }

        var position = await ReadClaimablePositionAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false);
        if (position is not null)
        {
            return new JobQueuePosition(jobId, JobQueuePositionKind.Claimable, position, "Job is currently claimable.");
        }

        return new JobQueuePosition(jobId, JobQueuePositionKind.BlockedByConcurrency, Position: null, "Job is blocked by concurrency group limits.");
    }

    public static async ValueTask<IReadOnlyDictionary<Guid, JobQueuePosition>> ReadQueuePositionsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        IReadOnlyList<PostgresJobInspectionRow> rows,
        DateTimeOffset? nowUtc,
        CancellationToken cancellationToken)
    {
        var positions = new Dictionary<Guid, JobQueuePosition>(rows.Count);
        var readyQueuedRows = new List<PostgresJobInspectionRow>();
        var queuedJobIds = rows
            .Where(static row => row.State == JobState.Queued)
            .Select(static row => row.JobId)
            .Distinct()
            .ToArray();
        var dependencyBlockedJobIds = await ReadDependencyBlockedJobIdsAsync(
            context,
            connection,
            queuedJobIds,
            cancellationToken)
          .ConfigureAwait(false);
        DateTimeOffset? resolvedNowUtc = null;

        foreach (var row in rows)
        {
            switch (row.State)
            {
                case JobState.Canceled:
                    positions[row.JobId] = new JobQueuePosition(row.JobId, JobQueuePositionKind.Canceled, Position: null, "Job was canceled.");
                    break;

                case JobState.Completed:
                case JobState.Failed:
                    positions[row.JobId] = new JobQueuePosition(row.JobId, JobQueuePositionKind.Terminal, Position: null, "Job is terminal.");
                    break;

                case JobState.Claimed:
                    positions[row.JobId] = new JobQueuePosition(row.JobId, JobQueuePositionKind.Claimed, Position: null, "Job is currently claimed.");
                    break;

                case JobState.Queued:
                    if (dependencyBlockedJobIds.Contains(row.JobId))
                    {
                        positions[row.JobId] = new JobQueuePosition(
                          row.JobId,
                          JobQueuePositionKind.WaitingForDependencies,
                          Position: null,
                          "Job is waiting for prerequisite jobs to become terminal.");
                        break;
                    }

                    resolvedNowUtc ??= nowUtc ?? await PostgresJobInspectionRows.ReadCurrentTimestampAsync(connection, cancellationToken).ConfigureAwait(false);
                    if (row.NotBeforeUtc is { } notBeforeUtc && notBeforeUtc > resolvedNowUtc.Value)
                    {
                        positions[row.JobId] = row.FailedAtUtc is null
                          ? new JobQueuePosition(row.JobId, JobQueuePositionKind.Delayed, Position: null, $"Job is delayed until {notBeforeUtc:O}.")
                          : new JobQueuePosition(row.JobId, JobQueuePositionKind.RetryWaiting, Position: null, $"Job is waiting to retry until {notBeforeUtc:O}.");
                    }
                    else
                    {
                        readyQueuedRows.Add(row);
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(rows), row.State, "Job state is not supported.");
            }
        }

        if (readyQueuedRows.Count == 0)
        {
            return positions;
        }

        resolvedNowUtc ??= nowUtc ?? await PostgresJobInspectionRows.ReadCurrentTimestampAsync(connection, cancellationToken).ConfigureAwait(false);
        var readyJobIds = readyQueuedRows.Select(static row => row.JobId).Distinct().ToArray();
        var claimablePositions = await ReadClaimablePositionsAsync(context, connection, readyJobIds, resolvedNowUtc.Value, cancellationToken)
          .ConfigureAwait(false);

        foreach (var row in readyQueuedRows)
        {
            positions[row.JobId] = claimablePositions.TryGetValue(row.JobId, out var position)
              ? new JobQueuePosition(row.JobId, JobQueuePositionKind.Claimable, position, "Job is currently claimable.")
              : new JobQueuePosition(row.JobId, JobQueuePositionKind.BlockedByConcurrency, Position: null, "Job is blocked by concurrency group limits.");
        }

        return positions;
    }

    public static async ValueTask<IReadOnlyDictionary<Guid, long>> ReadClaimablePositionsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return new Dictionary<Guid, long>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          with claimable as (
              select
                  job.job_id,
                  row_number() over (order by job.priority desc, job.enqueue_sequence asc) as position
              from {context.Names.Jobs} job
              where job.state = 'Queued'
                and (job.not_before_utc is null or job.not_before_utc <= @now_utc)
                and not exists (
                    select 1
                    from {context.Names.JobDependencies} dependency
                    join {context.Names.Jobs} prerequisite on prerequisite.job_id = dependency.prerequisite_job_id
                    where dependency.job_id = job.job_id
                      and prerequisite.state not in ('Completed', 'Failed', 'Canceled')
                )
                and not exists (
                    select 1
                    from {context.Names.JobConcurrencyGroups} job_group
                    left join {context.Names.ConcurrencyGroups} concurrency_group on concurrency_group.group_key = job_group.group_key
                    where job_group.job_id = job.job_id
                      and coalesce(concurrency_group.in_use_count, 0) >= coalesce(concurrency_group.effective_limit, 1)
                )
          )
          select job_id, position
          from claimable
          where job_id = any(@job_ids);
          """;
        command.Parameters.AddWithValue("now_utc", nowUtc);
        command.Parameters.AddWithValue("job_ids", jobIds);

        var positions = new Dictionary<Guid, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            positions.Add(reader.GetGuid(0), reader.GetInt64(1));
        }

        return positions;
    }

    public static async ValueTask<long?> ReadClaimablePositionAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          with claimable as (
              select
                  job.job_id,
                  row_number() over (order by job.priority desc, job.enqueue_sequence asc) as position
              from {context.Names.Jobs} job
              where job.state = 'Queued'
                and (job.not_before_utc is null or job.not_before_utc <= transaction_timestamp())
                and not exists (
                    select 1
                    from {context.Names.JobDependencies} dependency
                    join {context.Names.Jobs} prerequisite on prerequisite.job_id = dependency.prerequisite_job_id
                    where dependency.job_id = job.job_id
                      and prerequisite.state not in ('Completed', 'Failed', 'Canceled')
                )
                and not exists (
                    select 1
                    from {context.Names.JobConcurrencyGroups} job_group
                    left join {context.Names.ConcurrencyGroups} concurrency_group on concurrency_group.group_key = job_group.group_key
                    where job_group.job_id = job.job_id
                      and coalesce(concurrency_group.in_use_count, 0) >= coalesce(concurrency_group.effective_limit, 1)
                )
          )
          select position
          from claimable
          where job_id = @job_id;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async ValueTask<bool> HasUnfinishedPrerequisitesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select exists (
              select 1
              from {context.Names.JobDependencies} dependency
              join {context.Names.Jobs} prerequisite on prerequisite.job_id = dependency.prerequisite_job_id
              where dependency.job_id = @job_id
                and prerequisite.state not in ('Completed', 'Failed', 'Canceled')
          );
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return a dependency status."));
    }

    public static async ValueTask<HashSet<Guid>> ReadDependencyBlockedJobIdsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select distinct dependency.job_id
          from {context.Names.JobDependencies} dependency
          join {context.Names.Jobs} prerequisite on prerequisite.job_id = dependency.prerequisite_job_id
          where dependency.job_id = any(@job_ids)
            and prerequisite.state not in ('Completed', 'Failed', 'Canceled');
          """;
        command.Parameters.AddWithValue("job_ids", jobIds);

        var blocked = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            blocked.Add(reader.GetGuid(0));
        }

        return blocked;
    }
}
