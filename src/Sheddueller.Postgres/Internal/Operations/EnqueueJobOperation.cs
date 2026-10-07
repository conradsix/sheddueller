namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller.Enqueueing;
using Sheddueller.Storage;

internal static class EnqueueJobOperation
{
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

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await PostgresEnqueueStaging.CreateStagingTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.CopyJobsAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.CopyGroupsAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.CopyDependenciesAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.CopyTagsAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.CopyEventsAsync(connection, requestSnapshot, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.EnsureNoDuplicateJobIdsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.LockIdempotencyKeysAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);

        var results = await PostgresEnqueueStaging.InsertStagedJobsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.InsertStagedGroupsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.InsertStagedDependenciesAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.InsertStagedTagsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgresEnqueueStaging.InsertStagedEventsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgresMetricsRollups.RecordStagedQueuedJobsAsync(context, connection, transaction, cancellationToken)
          .ConfigureAwait(false);

        await PostgresEnqueueStaging.NotifyStagedEventsAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return results;
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

        DependencyGraph.ValidateAcyclic(requestsById.Keys, jobId => requestsById[jobId].PrerequisiteJobIds ?? [], EqualityComparer<Guid>.Default, nameof(requests));
    }

}
