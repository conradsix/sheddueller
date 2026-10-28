namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using NpgsqlTypes;

using Sheddueller.Enqueueing;
using Sheddueller.Storage;

internal static class EnqueueJobOperation
{
    /// <summary>
    /// The jobs table columns that every job insert writes, in the order of its values.
    /// </summary>
    private const string JobInsertColumnsSql =
      """
      job_id,
      state,
      priority,
      enqueued_at_utc,
      not_before_utc,
      service_type,
      method_name,
      method_parameter_types,
      invocation_target_kind,
      method_parameter_bindings,
      serialized_arguments_content_type,
      serialized_arguments,
      attempt_count,
      max_attempts,
      retry_backoff_kind,
      retry_base_delay_ms,
      retry_max_delay_ms,
      source_schedule_key,
      scheduled_fire_at_utc,
      retry_clone_source_job_id,
      schedule_occurrence_kind,
      idempotency_key,
      job_event_sequence
      """;

    public static async ValueTask<EnqueueJobResult> ExecuteAsync(
        PostgresOperationContext context,
        EnqueueJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = await ExecuteManyAsync(context, [request], cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    public static async ValueTask<IReadOnlyList<EnqueueJobResult>> ExecuteManyAsync(
        PostgresOperationContext context,
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return [];
        }

        var requestSnapshot = requests.ToArray();
        ValidateDependencyGraph(requestSnapshot);
        foreach (var request in requestSnapshot)
        {
            ArgumentNullException.ThrowIfNull(request);
            SubmissionValidator.ValidateIdempotencyKey(request.IdempotencyKey);

            if (request.IdempotencyKey is not null && request.NotBeforeUtc is not null)
            {
                throw new ArgumentException("Idempotent jobs cannot be delayed with NotBeforeUtc.", nameof(requests));
            }
        }

        foreach (var request in requestSnapshot)
        {
            foreach (var groupKey in request.ConcurrencyGroupKeys.Distinct(StringComparer.Ordinal))
            {
                SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);
            }
        }

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var results = requestSnapshot.Length == 1
          ? [await EnqueueOneAsync(context, connection, transaction, requestSnapshot[0], cancellationToken).ConfigureAwait(false)]
          : await EnqueueBatchAsync(context, connection, transaction, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Enqueues one job inside the caller's transaction, as <see cref="ExecuteAsync" /> does, but without waking
    /// workers: the caller wakes them once, after its other changes.
    /// </summary>
    /// <remarks>
    /// The caller validates the request; a recurring schedule's occurrence comes from a definition validated when it
    /// was stored.
    /// </remarks>
    public static ValueTask<EnqueueJobResult> EnqueueInTransactionAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnqueueJobRequest request,
        CancellationToken cancellationToken)
      => EnqueueOneAsync(context, connection, transaction, request, cancellationToken);

    /// <summary>
    /// Enqueues a single job with direct statements, which a batch's staging tables would only slow down. A single
    /// job has no dependencies: a prerequisite must be in the same batch, and a job cannot depend on itself.
    /// </summary>
    private static async ValueTask<EnqueueJobResult> EnqueueOneAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnqueueJobRequest request,
        CancellationToken cancellationToken)
    {
        var tags = SubmissionValidator.NormalizeJobTags(request.Tags);
        await EnsureJobDoesNotExistAsync(context, connection, transaction, request.JobId, cancellationToken).ConfigureAwait(false);

        if (request.IdempotencyKey is { } idempotencyKey)
        {
            await PostgresAdvisoryLocks.LockIdempotencyKeyAsync(connection, transaction, context.Options.SchemaName, idempotencyKey, cancellationToken)
              .ConfigureAwait(false);
            if (await ReadQueuedIdempotentJobAsync(context, connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return existing;
            }
        }

        var enqueueSequence = await InsertJobAsync(context, connection, transaction, request, cancellationToken).ConfigureAwait(false);
        await PostgresJobGroups.InsertJobGroupsAsync(context, connection, transaction, request.JobId, request.ConcurrencyGroupKeys, cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobTags.InsertJobTagsAsync(context, connection, transaction, request.JobId, tags, cancellationToken)
          .ConfigureAwait(false);

        // The job row starts with its first event sequence reserved for this event, as a batch's jobs do.
        await PostgresJobEvents.InsertAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(request.JobId, JobEventKind.Lifecycle, AttemptNumber: 0, Message: PostgresJobEventMessages.Queued),
          eventSequence: 1,
          cancellationToken)
          .ConfigureAwait(false);

        return new EnqueueJobResult(request.JobId, enqueueSequence, WasEnqueued: true);
    }

    private static async ValueTask EnsureJobDoesNotExistAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction,
          $"select exists (select 1 from {context.Names.Jobs} where job_id = @job_id);");
        command.Parameters.AddWithValue("job_id", jobId);

        if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
        {
            throw new InvalidOperationException($"Job '{jobId}' already exists.");
        }
    }

    /// <summary>
    /// Reads the oldest queued job with the idempotency key, which a new job with that key joins instead of being
    /// enqueued.
    /// </summary>
    private static async ValueTask<EnqueueJobResult?> ReadQueuedIdempotentJobAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction,
          $"""
          select job.job_id, job.enqueue_sequence
          from {context.Names.Jobs} job
          where job.idempotency_key = @idempotency_key
            and job.state = 'Queued'
          order by job.enqueue_sequence asc
          limit 1;
          """);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
          ? new EnqueueJobResult(reader.GetGuid(0), reader.GetInt64(1), WasEnqueued: false)
          : null;
    }

    private static async ValueTask<long> InsertJobAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnqueueJobRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction,
          $"""
          insert into {context.Names.Jobs} (
              {JobInsertColumnsSql})
          values (
              @job_id,
              'Queued',
              @priority,
              transaction_timestamp(),
              @not_before_utc,
              @service_type,
              @method_name,
              @method_parameter_types,
              @invocation_target_kind,
              @method_parameter_bindings,
              @serialized_arguments_content_type,
              @serialized_arguments,
              0,
              @max_attempts,
              @retry_backoff_kind,
              @retry_base_delay_ms,
              @retry_max_delay_ms,
              @source_schedule_key,
              @scheduled_fire_at_utc,
              @retry_clone_source_job_id,
              @schedule_occurrence_kind,
              @idempotency_key,
              1)
          returning enqueue_sequence;
          """);
        var parameters = command.Parameters;
        parameters.Add("job_id", NpgsqlDbType.Uuid).Value = request.JobId;
        parameters.Add("priority", NpgsqlDbType.Integer).Value = request.Priority;
        parameters.Add("not_before_utc", NpgsqlDbType.TimestampTz).Value = PostgresOperationContext.ToDbValue(request.NotBeforeUtc);
        parameters.Add("service_type", NpgsqlDbType.Text).Value = request.ServiceType;
        parameters.Add("method_name", NpgsqlDbType.Text).Value = request.MethodName;
        parameters.Add("method_parameter_types", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = request.MethodParameterTypes.ToArray();
        parameters.Add("invocation_target_kind", NpgsqlDbType.Text).Value = PostgresConversion.ToText(request.InvocationTargetKind);
        parameters.Add("method_parameter_bindings", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
          PostgresOperationContext.ToDbValue(request.MethodParameterBindings?.Select(PostgresConversion.ToText).ToArray());
        parameters.Add("serialized_arguments_content_type", NpgsqlDbType.Text).Value = request.SerializedArguments.ContentType;
        parameters.Add("serialized_arguments", NpgsqlDbType.Bytea).Value = request.SerializedArguments.Data;
        parameters.Add("max_attempts", NpgsqlDbType.Integer).Value = request.MaxAttempts;
        parameters.Add("retry_backoff_kind", NpgsqlDbType.Text).Value = PostgresOperationContext.ToDbValue(PostgresConversion.ToText(request.RetryBackoffKind));
        parameters.Add("retry_base_delay_ms", NpgsqlDbType.Bigint).Value = PostgresOperationContext.ToDbValue(PostgresConversion.ToMilliseconds(request.RetryBaseDelay));
        parameters.Add("retry_max_delay_ms", NpgsqlDbType.Bigint).Value = PostgresOperationContext.ToDbValue(PostgresConversion.ToMilliseconds(request.RetryMaxDelay));
        parameters.Add("source_schedule_key", NpgsqlDbType.Text).Value = PostgresOperationContext.ToDbValue(request.SourceScheduleKey);
        parameters.Add("scheduled_fire_at_utc", NpgsqlDbType.TimestampTz).Value = PostgresOperationContext.ToDbValue(request.ScheduledFireAtUtc);
        parameters.Add("retry_clone_source_job_id", NpgsqlDbType.Uuid).Value = PostgresOperationContext.ToDbValue(request.RetryCloneSourceJobId);
        parameters.Add("schedule_occurrence_kind", NpgsqlDbType.Text).Value =
          PostgresOperationContext.ToDbValue(request.ScheduleOccurrenceKind is { } occurrenceKind ? PostgresConversion.ToText(occurrenceKind) : null);
        parameters.Add("idempotency_key", NpgsqlDbType.Text).Value = PostgresOperationContext.ToDbValue(request.IdempotencyKey);

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return an enqueue sequence for the job."));
    }

    /// <summary>
    /// Enqueues a batch through staging tables that the batch is copied into, so that a large batch costs a fixed
    /// number of statements.
    /// </summary>
    private static async ValueTask<IReadOnlyList<EnqueueJobResult>> EnqueueBatchAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnqueueJobRequest[] requestSnapshot,
        CancellationToken cancellationToken)
    {
        await CreateStagingTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CopyJobsAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await CopyGroupsAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await CopyDependenciesAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await CopyTagsAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await CopyEventsAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await EnsureStagedJobsDoNotExistAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockIdempotencyKeysAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);

        var results = await InsertStagedJobsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertStagedGroupsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertStagedDependenciesAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertStagedTagsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertStagedEventsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgresMetricsRollups.RecordStagedQueuedJobsAsync(context, connection, transaction, cancellationToken)
          .ConfigureAwait(false);

        await NotifyStagedEventsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);

        return results;
    }

    private static async ValueTask CreateStagingTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          """
          create temp table sheddueller_enqueue_jobs (
              ordinal integer not null,
              job_id uuid not null,
              priority integer not null,
              not_before_utc timestamptz null,
              service_type text not null,
              method_name text not null,
              method_parameter_types text[] not null,
              invocation_target_kind text not null,
              method_parameter_bindings text[] null,
              serialized_arguments_content_type text not null,
              serialized_arguments bytea not null,
              max_attempts integer not null,
              retry_backoff_kind text null,
              retry_base_delay_ms bigint null,
              retry_max_delay_ms bigint null,
              source_schedule_key text null,
              scheduled_fire_at_utc timestamptz null,
              retry_clone_source_job_id uuid null,
              schedule_occurrence_kind text null,
              idempotency_key text null
          ) on commit drop;

          create temp table sheddueller_enqueue_groups (
              job_id uuid not null,
              group_key text not null
          ) on commit drop;

          create temp table sheddueller_enqueue_dependencies (
              job_id uuid not null,
              prerequisite_job_id uuid not null
          ) on commit drop;

          create temp table sheddueller_enqueue_tags (
              job_id uuid not null,
              ordinal integer not null,
              name text not null,
              value text not null
          ) on commit drop;

          create temp table sheddueller_enqueue_events (
              ordinal integer not null,
              job_id uuid not null,
              event_id uuid not null
          ) on commit drop;

          create temp table sheddueller_enqueue_results (
              ordinal integer not null primary key,
              job_id uuid not null,
              enqueue_sequence bigint not null,
              was_enqueued boolean not null
          ) on commit drop;
          """,
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);

    private static async ValueTask CopyJobsAsync(
        NpgsqlConnection connection,
        EnqueueJobRequest[] requests,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
          """
          copy sheddueller_enqueue_jobs (
              ordinal,
              job_id,
              priority,
              not_before_utc,
              service_type,
              method_name,
              method_parameter_types,
              invocation_target_kind,
              method_parameter_bindings,
              serialized_arguments_content_type,
              serialized_arguments,
              max_attempts,
              retry_backoff_kind,
              retry_base_delay_ms,
              retry_max_delay_ms,
              source_schedule_key,
              scheduled_fire_at_utc,
              retry_clone_source_job_id,
              schedule_occurrence_kind,
              idempotency_key)
          from stdin (format binary)
          """,
          cancellationToken)
          .ConfigureAwait(false);

        for (var i = 0; i < requests.Length; i++)
        {
            var request = requests[i];
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(i, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(request.JobId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(request.Priority, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, request.NotBeforeUtc, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(request.ServiceType, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(request.MethodName, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(request.MethodParameterTypes.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(PostgresConversion.ToText(request.InvocationTargetKind), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableArrayAsync(
              importer,
              request.MethodParameterBindings?.Select(PostgresConversion.ToText).ToArray(),
              NpgsqlDbType.Array | NpgsqlDbType.Text,
              cancellationToken)
              .ConfigureAwait(false);
            await importer.WriteAsync(request.SerializedArguments.ContentType, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(request.SerializedArguments.Data, NpgsqlDbType.Bytea, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(request.MaxAttempts, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, PostgresConversion.ToText(request.RetryBackoffKind), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, PostgresConversion.ToMilliseconds(request.RetryBaseDelay), NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, PostgresConversion.ToMilliseconds(request.RetryMaxDelay), NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, request.SourceScheduleKey, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, request.ScheduledFireAtUtc, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, request.RetryCloneSourceJobId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(
              importer,
              request.ScheduleOccurrenceKind is null ? null : PostgresConversion.ToText(request.ScheduleOccurrenceKind.Value),
              NpgsqlDbType.Text,
              cancellationToken)
              .ConfigureAwait(false);
            await WriteNullableAsync(importer, request.IdempotencyKey, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CopyGroupsAsync(
        NpgsqlConnection connection,
        EnqueueJobRequest[] requests,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
          """
          copy sheddueller_enqueue_groups (
              job_id,
              group_key)
          from stdin (format binary)
          """,
          cancellationToken)
          .ConfigureAwait(false);

        foreach (var request in requests)
        {
            foreach (var groupKey in request.ConcurrencyGroupKeys.Distinct(StringComparer.Ordinal))
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(request.JobId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(groupKey, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            }
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CopyTagsAsync(
        NpgsqlConnection connection,
        EnqueueJobRequest[] requests,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
          """
          copy sheddueller_enqueue_tags (
              job_id,
              ordinal,
              name,
              value)
          from stdin (format binary)
          """,
          cancellationToken)
          .ConfigureAwait(false);

        foreach (var request in requests)
        {
            var tags = SubmissionValidator.NormalizeJobTags(request.Tags);
            for (var i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(request.JobId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(i, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(tag.Name, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(tag.Value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            }
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CopyDependenciesAsync(
        NpgsqlConnection connection,
        EnqueueJobRequest[] requests,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
          """
          copy sheddueller_enqueue_dependencies (
              job_id,
              prerequisite_job_id)
          from stdin (format binary)
          """,
          cancellationToken)
          .ConfigureAwait(false);

        foreach (var request in requests)
        {
            foreach (var prerequisiteJobId in request.PrerequisiteJobIds ?? [])
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(request.JobId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(prerequisiteJobId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            }
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CopyEventsAsync(
        NpgsqlConnection connection,
        EnqueueJobRequest[] requests,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
          """
          copy sheddueller_enqueue_events (
              ordinal,
              job_id,
              event_id)
          from stdin (format binary)
          """,
          cancellationToken)
          .ConfigureAwait(false);

        for (var i = 0; i < requests.Length; i++)
        {
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(i, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(requests[i].JobId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureStagedJobsDoNotExistAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var existingCommand = PostgresCommands.Create(
          connection,
          transaction,
          $"""
          select staged.job_id
          from sheddueller_enqueue_jobs staged
          join {context.Names.Jobs} job on job.job_id = staged.job_id
          limit 1;
          """);

        if (await existingCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid existingJobId)
        {
            throw new InvalidOperationException($"Job '{existingJobId}' already exists.");
        }
    }

    private static async ValueTask LockIdempotencyKeysAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          """
          select pg_advisory_xact_lock(hashtextextended(@schema_name || '|' || idempotency_key, @idempotency_key_hash_seed))
          from (
              select distinct idempotency_key
              from sheddueller_enqueue_jobs
              where idempotency_key is not null
              order by idempotency_key
          ) keys;
          """,
          command =>
          {
              command.Parameters.AddWithValue("schema_name", context.Options.SchemaName);
              command.Parameters.AddWithValue("idempotency_key_hash_seed", PostgresAdvisoryLocks.IdempotencyKeyHashSeed);
          },
          cancellationToken)
          .ConfigureAwait(false);

    private static async ValueTask<IReadOnlyList<EnqueueJobResult>> InsertStagedJobsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into sheddueller_enqueue_results (ordinal, job_id, enqueue_sequence, was_enqueued)
          select staged.ordinal, existing_job.job_id, existing_job.enqueue_sequence, false
          from sheddueller_enqueue_jobs staged
          join lateral (
              select job.job_id, job.enqueue_sequence
              from {context.Names.Jobs} job
              where staged.idempotency_key is not null
                and job.idempotency_key = staged.idempotency_key
                and job.state = 'Queued'
              order by job.enqueue_sequence asc
              limit 1
          ) existing_job on true;

          insert into {context.Names.Jobs} (
              {JobInsertColumnsSql})
          select
              job_id,
              'Queued',
              priority,
              transaction_timestamp(),
              not_before_utc,
              service_type,
              method_name,
              method_parameter_types,
              invocation_target_kind,
              method_parameter_bindings,
              serialized_arguments_content_type,
              serialized_arguments,
              0,
              max_attempts,
              retry_backoff_kind,
              retry_base_delay_ms,
              retry_max_delay_ms,
              source_schedule_key,
              scheduled_fire_at_utc,
              retry_clone_source_job_id,
              schedule_occurrence_kind,
              idempotency_key,
              1
          from sheddueller_enqueue_jobs staged
          where not exists (
              select 1
              from sheddueller_enqueue_results result
              where result.ordinal = staged.ordinal
          )
            and (
                staged.idempotency_key is null
                or staged.ordinal = (
                    select min(candidate.ordinal)
                    from sheddueller_enqueue_jobs candidate
                    where candidate.idempotency_key = staged.idempotency_key
                      and not exists (
                          select 1
                          from sheddueller_enqueue_results candidate_result
                          where candidate_result.ordinal = candidate.ordinal
                      )
                )
            )
          order by ordinal;

          insert into sheddueller_enqueue_results (ordinal, job_id, enqueue_sequence, was_enqueued)
          select staged.ordinal, job.job_id, job.enqueue_sequence, true
          from sheddueller_enqueue_jobs staged
          join {context.Names.Jobs} job on job.job_id = staged.job_id
          where not exists (
              select 1
              from sheddueller_enqueue_results result
              where result.ordinal = staged.ordinal
          );

          insert into sheddueller_enqueue_results (ordinal, job_id, enqueue_sequence, was_enqueued)
          select staged.ordinal, representative_result.job_id, representative_result.enqueue_sequence, false
          from sheddueller_enqueue_jobs staged
          join lateral (
              select candidate.ordinal
              from sheddueller_enqueue_jobs candidate
              join sheddueller_enqueue_results candidate_result on candidate_result.ordinal = candidate.ordinal
              where candidate.idempotency_key = staged.idempotency_key
              order by candidate.ordinal asc
              limit 1
          ) representative on true
          join sheddueller_enqueue_results representative_result on representative_result.ordinal = representative.ordinal
          where staged.idempotency_key is not null
            and not exists (
                select 1
                from sheddueller_enqueue_results result
                where result.ordinal = staged.ordinal
            );
          """,
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);

        await using var command = PostgresCommands.Create(
          connection,
          transaction,
          """
          select job_id, enqueue_sequence, was_enqueued
          from sheddueller_enqueue_results
          order by ordinal;
          """);

        var results = new List<EnqueueJobResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new EnqueueJobResult(reader.GetGuid(0), reader.GetInt64(1), reader.GetBoolean(2)));
        }

        return results;
    }

    private static async ValueTask InsertStagedGroupsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.JobConcurrencyGroups} (job_id, group_key)
          select distinct job_group.job_id, job_group.group_key
          from sheddueller_enqueue_groups job_group
          join sheddueller_enqueue_results result on result.job_id = job_group.job_id
            and result.was_enqueued = true
          where result.was_enqueued = true
          on conflict (job_id, group_key) do nothing;
          """,
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);

    private static async ValueTask InsertStagedTagsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.JobTags} (job_id, ordinal, name, value)
          select tag.job_id, tag.ordinal, tag.name, tag.value
          from sheddueller_enqueue_tags tag
          join sheddueller_enqueue_results result on result.job_id = tag.job_id
            and result.was_enqueued = true
          where result.was_enqueued = true
          on conflict (job_id, name, value) do nothing;
          """,
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);

    private static async ValueTask InsertStagedDependenciesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.JobDependencies} (job_id, prerequisite_job_id)
          select dependency.job_id, dependency.prerequisite_job_id
          from sheddueller_enqueue_dependencies dependency
          join sheddueller_enqueue_results dependent_result on dependent_result.job_id = dependency.job_id
          join sheddueller_enqueue_results prerequisite_result on prerequisite_result.job_id = dependency.prerequisite_job_id
          where dependent_result.was_enqueued = true
            and prerequisite_result.was_enqueued = true
          on conflict (job_id, prerequisite_job_id) do nothing;
          """,
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);

    private static async ValueTask InsertStagedEventsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.JobEvents} (
              job_id,
              event_sequence,
              event_id,
              kind,
              occurred_at_utc,
              attempt_number,
              log_level,
              message,
              progress_percent,
              fields)
          select
              staged_event.job_id,
              1,
              staged_event.event_id,
              'Lifecycle',
              transaction_timestamp(),
              0,
              null,
              @message,
              null,
              null
          from sheddueller_enqueue_events staged_event
          join sheddueller_enqueue_results result on result.ordinal = staged_event.ordinal
          where result.was_enqueued = true
          order by staged_event.ordinal;
          """,
          command => command.Parameters.AddWithValue("message", PostgresJobEventMessages.Queued),
          cancellationToken)
          .ConfigureAwait(false);

    // PostgreSQL queues notifications inside the transaction and only publishes them on commit.
    private static async ValueTask NotifyStagedEventsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          select pg_notify(
              @job_event_channel,
              @schema_name || '|' || replace(job_event.job_id::text, '-', '') || '|' || job_event.event_sequence::text)
          from {context.Names.JobEvents} job_event
          join sheddueller_enqueue_events staged_events on staged_events.event_id = job_event.event_id
          order by staged_events.ordinal;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_event_channel", PostgresNames.JobEventChannel);
              command.Parameters.AddWithValue("schema_name", context.Options.SchemaName);
          },
          cancellationToken)
          .ConfigureAwait(false);

    private static async ValueTask WriteNullableAsync<T>(
        NpgsqlBinaryImporter importer,
        T? value,
        NpgsqlDbType dbType,
        CancellationToken cancellationToken)
      where T : struct
    {
        if (value is null)
        {
            await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await importer.WriteAsync(value.Value, dbType, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateDependencyGraph(IReadOnlyList<EnqueueJobRequest> requests)
    {
        var requestsById = new Dictionary<Guid, EnqueueJobRequest>();
        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!requestsById.TryAdd(request.JobId, request))
            {
                throw new InvalidOperationException($"Job '{request.JobId}' appears more than once in the batch.");
            }
        }

        var hasDependencies = requests.Any(static request => request.PrerequisiteJobIds is { Count: > 0 });
        if (hasDependencies && requests.Any(static request => request.IdempotencyKey is not null))
        {
            throw new ArgumentException("Jobs in a dependency graph cannot use idempotency.", nameof(requests));
        }

        foreach (var request in requests)
        {
            var prerequisites = request.PrerequisiteJobIds ?? [];
            if (prerequisites.Count != prerequisites.Distinct().Count())
            {
                throw new ArgumentException($"Job '{request.JobId}' contains duplicate prerequisites.", nameof(requests));
            }

            foreach (var prerequisiteJobId in prerequisites)
            {
                if (prerequisiteJobId == request.JobId)
                {
                    throw new ArgumentException($"Job '{request.JobId}' cannot depend on itself.", nameof(requests));
                }

                if (!requestsById.ContainsKey(prerequisiteJobId))
                {
                    throw new ArgumentException(
                      $"Prerequisite job '{prerequisiteJobId}' for job '{request.JobId}' is not included in the batch.",
                      nameof(requests));
                }
            }
        }

        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        foreach (var request in requests)
        {
            visit(request.JobId);
        }

        void visit(Guid jobId)
        {
            if (visited.Contains(jobId))
            {
                return;
            }

            if (!visiting.Add(jobId))
            {
                throw new ArgumentException("Job dependency graphs cannot contain cycles.", nameof(requests));
            }

            foreach (var prerequisiteJobId in requestsById[jobId].PrerequisiteJobIds ?? [])
            {
                visit(prerequisiteJobId);
            }

            visiting.Remove(jobId);
            visited.Add(jobId);
        }
    }

    private static async ValueTask WriteNullableAsync(
        NpgsqlBinaryImporter importer,
        string? value,
        NpgsqlDbType dbType,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await importer.WriteAsync(value, dbType, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteNullableArrayAsync(
        NpgsqlBinaryImporter importer,
        string[]? value,
        NpgsqlDbType dbType,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await importer.WriteAsync(value, dbType, cancellationToken).ConfigureAwait(false);
    }
}
