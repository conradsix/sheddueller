namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller.Storage;

internal static class MarkJobCompletedOperation
{
    public static async ValueTask<bool> ExecuteAsync(
        PostgresOperationContext context,
        CompleteJobRequest request,
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

        // The claim read locked the job row for this transaction, so the update below targets that row directly and
        // always applies: the group decrement can never be committed without the matching state change.
        await PostgresJobGroups.DecrementGroupsAsync(context, connection, transaction, job.GroupKeys, cancellationToken).ConfigureAwait(false);
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.Jobs}
          set state = 'Completed',
              completed_at_utc = @completed_at_utc
          where job_id = @job_id;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_id", job.JobId);
              command.Parameters.AddWithValue("completed_at_utc", request.CompletedAtUtc);
          },
          cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(job.JobId, JobEventKind.AttemptCompleted, job.AttemptCount, Message: "Attempt completed"),
          cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(job.JobId, JobEventKind.Lifecycle, job.AttemptCount, Message: PostgresJobEventMessages.Completed),
          cancellationToken)
          .ConfigureAwait(false);
        await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
