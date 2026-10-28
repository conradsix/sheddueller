namespace Sheddueller.Postgres.Tests.ProviderContracts;

using Sheddueller.Inspection.Jobs;
using Sheddueller.ProviderContracts;
using Sheddueller.Storage;

public sealed class PostgresJobRetentionStoreContractTests(PostgresFixture fixture) : JobRetentionStoreContractTests
{
    protected override async ValueTask<JobRetentionStoreContractContext> CreateRetentionContextAsync()
    {
        var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        return new JobRetentionStoreContractContext(
          context.Store,
          (IJobRetentionStore)context.Store,
          (IJobInspectionReader)context.Store,
          context);
    }
}
