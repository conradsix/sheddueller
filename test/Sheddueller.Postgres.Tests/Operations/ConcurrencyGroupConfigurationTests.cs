namespace Sheddueller.Postgres.Tests.Operations;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

public sealed class ConcurrencyGroupConfigurationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Configuration_MissingGroup_ReturnsOneWithoutCreatingRows()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var reader = context.Provider.GetRequiredService<IConcurrencyGroupConfigurationReader>();

        (await reader.GetConfigurationAsync("missing")).ShouldBe(new(null, null, 1));
        (await context.ReadConcurrencyGroupAsync("missing")).ShouldBeNull();
    }

    [Fact]
    public async Task Configuration_DefaultAndOverride_SurvivesDefaultChangesAndReset()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var manager = context.Provider.GetRequiredService<IConcurrencyGroupManager>();
        var reader = context.Provider.GetRequiredService<IConcurrencyGroupConfigurationReader>();
        await manager.SetDefaultLimitAsync("ai", 3);
        (await reader.GetConfigurationAsync("ai")).ShouldBe(new(3, null, 3));
        await manager.SetLimitAsync("ai", 5);
        await manager.SetDefaultLimitAsync("ai", 4);
        (await reader.GetConfigurationAsync("ai")).ShouldBe(new(4, 5, 5));
        await manager.ClearLimitOverrideAsync("ai");
        (await reader.GetConfigurationAsync("ai")).ShouldBe(new(4, null, 4));
    }
}
