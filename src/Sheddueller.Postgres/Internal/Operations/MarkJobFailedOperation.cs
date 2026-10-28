namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller.Storage;

internal static class MarkJobFailedOperation
{
    public static async ValueTask<bool> ExecuteAsync(
        PostgresOperationContext context,
        FailJobRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var job = await PostgresClaimedJobs.TryLockCurrentClaimAsync(
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

        await PostgresJobGroups.DecrementGroupsAsync(context, connection, transaction, job.GroupKeys, cancellationToken).ConfigureAwait(false);
        var lifecycleMessage = await PostgresClaimedJobs.ApplyFailedAttemptAsync(
          context,
          connection,
          transaction,
          job,
          request.FailedAtUtc,
          request.Failure,
          cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(job.JobId, JobEventKind.AttemptFailed, job.AttemptCount, Message: request.Failure.Message),
          cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(
            job.JobId,
            JobEventKind.Lifecycle,
            job.AttemptCount,
            Message: lifecycleMessage),
          cancellationToken)
          .ConfigureAwait(false);
        await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
