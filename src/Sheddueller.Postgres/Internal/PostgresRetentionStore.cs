namespace Sheddueller.Postgres.Internal;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;

using Sheddueller;
using Sheddueller.Postgres.Internal.Operations;
using Sheddueller.Storage;

/// <summary>
/// PostgreSQL retention: terminal job and job event cleanup, the metrics cleanup that rides on job retention, dead
/// worker node removal, and the stored cleanup configuration.
/// </summary>
/// <param name="context">The provider's operation context.</param>
/// <param name="logger">
/// Logs under the <see cref="PostgresJobStore" /> category, which these operations logged under before they moved here.
/// </param>
internal sealed class PostgresRetentionStore(
    PostgresOperationContext context,
    ILogger<PostgresJobStore> logger)
    : IJobEventRetentionStore,
    IJobRetentionStore,
    IWorkerNodeRetentionStore,
    IShedduellerCleanupConfigurationStore
{
    public ValueTask<int> CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
      => PostgresJobRetentionOperation.CleanupEventsAsync(
        context,
        retention,
        PostgresJobRetentionOperation.JobEventCleanupBatchSize,
        cancellationToken);

    public ValueTask<int> CleanupWorkerNodesAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        return PostgresWorkerOperations.CleanupWorkerNodesAsync(context, retention, cancellationToken);
    }

    public ValueTask<JobRetentionCleanupResult> CleanupTerminalJobsAsync(
        JobRetentionCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return this.CleanupTerminalJobsCoreAsync(request, cancellationToken);
    }

    public ValueTask<ShedduellerCleanupConfiguration> GetCleanupConfigurationAsync(
        ShedduellerCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultConfiguration);

        return PostgresCleanupConfigurationOperation.GetAsync(context, defaultConfiguration, cancellationToken);
    }

    public ValueTask<JobRetentionCleanupConfiguration> GetJobRetentionCleanupConfigurationAsync(
        JobRetentionCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultConfiguration);

        return PostgresCleanupConfigurationOperation.GetJobRetentionAsync(context, defaultConfiguration, cancellationToken);
    }

    public ValueTask<JobEventCleanupConfiguration> GetJobEventCleanupConfigurationAsync(
        JobEventCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultConfiguration);

        return PostgresCleanupConfigurationOperation.GetJobEventsAsync(context, defaultConfiguration, cancellationToken);
    }

    public ValueTask<MetricsCleanupConfiguration> GetMetricsCleanupConfigurationAsync(
        MetricsCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultConfiguration);

        return PostgresCleanupConfigurationOperation.GetMetricsAsync(context, defaultConfiguration, cancellationToken);
    }

    public ValueTask SetCleanupConfigurationAsync(
        ShedduellerCleanupConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return PostgresCleanupConfigurationOperation.SetAsync(context, configuration, cancellationToken);
    }

    private async ValueTask<JobRetentionCleanupResult> CleanupTerminalJobsCoreAsync(
        JobRetentionCleanupRequest request,
        CancellationToken cancellationToken)
    {
        var result = await PostgresJobRetentionOperation.ExecuteAsync(context, request, cancellationToken).ConfigureAwait(false);
        await this.CleanupMetricsAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    // Metrics rollups have no cleanup service of their own. The job retention service runs on worker and dashboard
    // hosts alike, so its passes prune metrics too; the cleanup itself keeps to the configured metrics interval.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A metrics cleanup failure must not fail the job retention pass that ran it.")]
    private async ValueTask CleanupMetricsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await PostgresCleanupConfigurationOperation
              .GetMetricsAsync(context, MetricsCleanupConfiguration.Default, cancellationToken)
              .ConfigureAwait(false);
            await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await PostgresMetricsRollups.CleanupAsync(context, connection, configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.PostgresMetricsCleanupFailed(exception, context.Options.SchemaName);
        }
    }
}
