namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller;
using Sheddueller.Storage;

internal static class TriggerRecurringScheduleOperation
{
    public static async ValueTask<RecurringScheduleTriggerResult> ExecuteAsync(
        PostgresOperationContext context,
        TriggerRecurringScheduleRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var schedule = await PostgresSchedules.ReadScheduleDefinitionForUpdateAsync(
          context,
          connection,
          transaction,
          request.ScheduleKey,
          cancellationToken)
          .ConfigureAwait(false);

        if (schedule is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RecurringScheduleTriggerResult(RecurringScheduleTriggerStatus.NotFound);
        }

        if (schedule.OverlapMode == RecurringOverlapMode.Skip
          && await PostgresSchedules.HasNonTerminalOccurrenceAsync(context, connection, transaction, schedule.ScheduleKey, cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RecurringScheduleTriggerResult(RecurringScheduleTriggerStatus.SkippedActiveOccurrence);
        }

        var retry = PostgresRetryPolicies.Normalize(schedule.RetryPolicy ?? request.DefaultRetryPolicy);
        var jobId = Guid.NewGuid();
        var enqueued = await PostgresSchedules.EnqueueOccurrenceAsync(
          context,
          connection,
          transaction,
          schedule,
          retry,
          jobId,
          scheduledFireAtUtc: null,
          occurrenceKind: ScheduleOccurrenceKind.ManualTrigger,
          cancellationToken)
          .ConfigureAwait(false);

        await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RecurringScheduleTriggerResult(RecurringScheduleTriggerStatus.Enqueued, jobId, enqueued.EnqueueSequence);
    }
}
