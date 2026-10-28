namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller.Scheduling;
using Sheddueller.Storage;

internal static class MaterializeDueRecurringSchedulesOperation
{
    public static async ValueTask<int> ExecuteAsync(
        PostgresOperationContext context,
        MaterializeDueRecurringSchedulesRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var schedules = await PostgresSchedules.ReadDueSchedulesAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        var transactionTimestamp = await PostgresOperationContext.ReadTransactionTimestampAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var materialized = 0;

        foreach (var schedule in schedules)
        {
            var canMaterialize = schedule.OverlapMode == RecurringOverlapMode.Allow
              || !await PostgresSchedules.HasNonTerminalOccurrenceAsync(context, connection, transaction, schedule.ScheduleKey, cancellationToken)
                .ConfigureAwait(false);

            if (canMaterialize)
            {
                var retry = PostgresRetryPolicies.Normalize(schedule.RetryPolicy ?? request.DefaultRetryPolicy);
                var jobId = Guid.NewGuid();
                await PostgresSchedules.EnqueueOccurrenceAsync(
                  context,
                  connection,
                  transaction,
                  schedule,
                  retry,
                  jobId,
                  schedule.NextFireAtUtc,
                  ScheduleOccurrenceKind.Automatic,
                  cancellationToken)
                  .ConfigureAwait(false);
                materialized++;
            }

            var nextFireAtUtc = CronSchedule.GetNextOccurrenceAfter(schedule.CronExpression, transactionTimestamp);
            await PostgresOperationContext.ExecuteCountAsync(
              connection,
              transaction,
              $"""
              update {context.Names.RecurringSchedules}
              set next_fire_at_utc = @next_fire_at_utc,
                  updated_at_utc = transaction_timestamp()
              where schedule_key = @schedule_key;
              """,
              command =>
              {
                  command.Parameters.AddWithValue("schedule_key", schedule.ScheduleKey);
                  command.Parameters.AddWithValue("next_fire_at_utc", nextFireAtUtc);
              },
              cancellationToken)
              .ConfigureAwait(false);
        }

        if (materialized > 0)
        {
            await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return materialized;
    }
}
