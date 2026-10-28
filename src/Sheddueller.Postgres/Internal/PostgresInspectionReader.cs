namespace Sheddueller.Postgres.Internal;

using Microsoft.Extensions.Options;

using Sheddueller;
using Sheddueller.Inspection.ConcurrencyGroups;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Metrics;
using Sheddueller.Inspection.Nodes;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Postgres.Internal.Operations;
using Sheddueller.Storage;

/// <summary>
/// The PostgreSQL inspection readers behind the dashboard: jobs, schedules, concurrency groups, nodes and metrics.
/// </summary>
internal sealed class PostgresInspectionReader(
    PostgresOperationContext context,
    IOptions<ShedduellerOptions> shedduellerOptions)
    : IJobInspectionReader,
    IScheduleInspectionReader,
    IConcurrencyGroupInspectionReader,
    INodeInspectionReader,
    IMetricsInspectionReader
{
    public ValueTask<JobInspectionOverview> GetOverviewAsync(
        CancellationToken cancellationToken = default)
      => PostgresJobInspectionOperation.GetOverviewAsync(context, cancellationToken);

    public ValueTask<JobInspectionPage> SearchJobsAsync(
        JobInspectionQuery query,
        CancellationToken cancellationToken = default)
      => PostgresJobInspectionOperation.SearchJobsAsync(context, query, cancellationToken);

    public ValueTask<JobInspectionDetail?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
      => PostgresJobInspectionOperation.GetJobAsync(context, jobId, cancellationToken);

    public ValueTask<JobQueuePosition> GetQueuePositionAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
      => PostgresJobInspectionOperation.GetQueuePositionAsync(context, jobId, cancellationToken);

    public IAsyncEnumerable<JobEvent> ReadEventsAsync(
        Guid jobId,
        JobEventReadOptions? options = null,
        CancellationToken cancellationToken = default)
      => PostgresJobInspectionOperation.ReadEventsAsync(context, jobId, options, cancellationToken);

    public ValueTask<ScheduleInspectionPage> SearchSchedulesAsync(
        ScheduleInspectionQuery query,
        CancellationToken cancellationToken = default)
      => PostgresScheduleInspectionOperation.SearchSchedulesAsync(context, query, cancellationToken);

    public ValueTask<ScheduleInspectionDetail?> GetScheduleAsync(
        string scheduleKey,
        CancellationToken cancellationToken = default)
      => PostgresScheduleInspectionOperation.GetScheduleAsync(context, scheduleKey, cancellationToken);

    public ValueTask<ConcurrencyGroupInspectionPage> SearchConcurrencyGroupsAsync(
        ConcurrencyGroupInspectionQuery query,
        CancellationToken cancellationToken = default)
      => PostgresConcurrencyGroupInspectionOperation.SearchAsync(context, query, cancellationToken);

    public ValueTask<ConcurrencyGroupInspectionDetail?> GetConcurrencyGroupAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
      => PostgresConcurrencyGroupInspectionOperation.GetAsync(context, groupKey, cancellationToken);

    public ValueTask<NodeInspectionPage> SearchNodesAsync(
        NodeInspectionQuery query,
        CancellationToken cancellationToken = default)
      => PostgresNodeInspectionOperation.SearchAsync(
        context,
        query,
        shedduellerOptions.Value.EffectiveStaleNodeThreshold,
        shedduellerOptions.Value.EffectiveDeadNodeThreshold,
        cancellationToken);

    public ValueTask<NodeInspectionDetail?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
      => PostgresNodeInspectionOperation.GetAsync(
        context,
        nodeId,
        shedduellerOptions.Value.EffectiveStaleNodeThreshold,
        shedduellerOptions.Value.EffectiveDeadNodeThreshold,
        cancellationToken);

    public ValueTask<MetricsInspectionSnapshot> GetMetricsAsync(
        MetricsInspectionQuery query,
        CancellationToken cancellationToken = default)
      => this.GetMetricsCoreAsync(query, cancellationToken);

    private async ValueTask<MetricsInspectionSnapshot> GetMetricsCoreAsync(
        MetricsInspectionQuery query,
        CancellationToken cancellationToken)
    {
        var cleanupConfiguration = await PostgresCleanupConfigurationOperation
          .GetMetricsAsync(context, MetricsCleanupConfiguration.Default, cancellationToken)
          .ConfigureAwait(false);

        return await PostgresMetricsInspectionOperation.GetAsync(
          context,
          query,
          cleanupConfiguration,
          shedduellerOptions.Value.EffectiveStaleNodeThreshold,
          shedduellerOptions.Value.EffectiveDeadNodeThreshold,
          cancellationToken)
          .ConfigureAwait(false);
    }
}
