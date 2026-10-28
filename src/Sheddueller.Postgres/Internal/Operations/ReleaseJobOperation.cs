namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller.Storage;

internal static class ReleaseJobOperation
{
    public static async ValueTask<bool> ExecuteAsync(
        PostgresOperationContext context,
        ReleaseJobRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var job = await PostgresClaimedJobs.TryReadCurrentClaimForFailureAsync(
          context,
          connection,
          transaction,
          request.JobId,
          request.NodeId,
          request.LeaseToken,
          cancellationToken)
          .ConfigureAwait(false);

        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        // The claim read locked the job row for this transaction, so the updates below target that row directly and
        // always apply: the group decrement can never be committed without the matching state change.
        await PostgresJobGroups.DecrementGroupsAsync(context, connection, transaction, job.GroupKeys, cancellationToken).ConfigureAwait(false);
        var supersededFailure = new JobFailureInfo(
          "Sheddueller.IdempotencySuperseded",
          "The job was superseded by a queued idempotent job.",
          null);
        var supersededMessage = await PostgresClaimedJobs.TryMarkSupersededByQueuedDuplicateAsync(
          context,
          connection,
          transaction,
          job,
          request.ReleasedAtUtc,
          supersededFailure,
          cancellationToken)
          .ConfigureAwait(false);
        if (supersededMessage is not null)
        {
            await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
              context,
              connection,
              transaction,
              new AppendJobEventRequest(job.JobId, JobEventKind.AttemptFailed, job.AttemptCount, Message: supersededFailure.Message),
              cancellationToken)
              .ConfigureAwait(false);
            await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
              context,
              connection,
              transaction,
              new AppendJobEventRequest(job.JobId, JobEventKind.Lifecycle, job.AttemptCount, Message: supersededMessage),
              cancellationToken)
              .ConfigureAwait(false);
            await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.Jobs}
          set state = 'Queued',
              attempt_count = greatest(0, attempt_count - 1),
              not_before_utc = null,
              claimed_by_node_id = null,
              claimed_at_utc = null,
              lease_token = null,
              lease_expires_at_utc = null,
              last_heartbeat_at_utc = null
          where job_id = @job_id;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_id", job.JobId);
          },
          cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(job.JobId, JobEventKind.Lifecycle, job.AttemptCount, Message: "Released"),
          cancellationToken)
          .ConfigureAwait(false);
        await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
