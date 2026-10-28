namespace Sheddueller.Postgres.Tests.ProviderContracts;

using Sheddueller.Inspection.ConcurrencyGroups;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Metrics;
using Sheddueller.Inspection.Nodes;
using Sheddueller.Inspection.Schedules;
using Sheddueller.ProviderContracts;
using Sheddueller.Storage;

public sealed class PostgresInspectionContractTests(PostgresFixture fixture) : InspectionContractTests
{
    protected override async ValueTask<InspectionContractContext> CreateContextAsync()
    {
        var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var reader = (IJobInspectionReader)context.Store;
        var eventSink = (IJobEventSink)context.Store;
        var retentionStore = (IJobEventRetentionStore)context.Store;
        var scheduleReader = (IScheduleInspectionReader)context.Store;
        var concurrencyGroupReader = (IConcurrencyGroupInspectionReader)context.Store;
        var nodeReader = (INodeInspectionReader)context.Store;
        var metricsReader = (IMetricsInspectionReader)context.Store;

        return new InspectionContractContext(
          context.Store,
          reader,
          eventSink,
          retentionStore,
          scheduleReader,
          concurrencyGroupReader,
          nodeReader,
          metricsReader,
          context);
    }
}
