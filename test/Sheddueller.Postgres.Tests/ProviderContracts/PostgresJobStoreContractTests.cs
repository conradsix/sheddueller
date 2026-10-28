namespace Sheddueller.Postgres.Tests.ProviderContracts;

using Sheddueller.ProviderContracts;

public sealed class PostgresJobStoreContractTests(PostgresFixture fixture) : JobStoreContractTests
{
    protected override async ValueTask<JobStoreContractContext> CreateContextAsync()
    {
        var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        return new JobStoreContractContext(context.Store, context.MakeScheduleDueAsync, context, context.InspectionReader);
    }
}
