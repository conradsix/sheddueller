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
        var reader = context.InspectionReader;
        var eventSink = context.EventSink;
        var retentionStore = context.EventRetentionStore;
        var scheduleReader = context.ScheduleReader;
        var concurrencyGroupReader = context.ConcurrencyGroupReader;
        var nodeReader = context.NodeReader;
        var metricsReader = context.MetricsReader;

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
