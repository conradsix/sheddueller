namespace Sheddueller.Testing.Tests;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

public sealed class FakeConcurrencyGroupConfigurationReaderTests
{
    [Fact]
    public async Task Configuration_TestRegistration_UsesConfiguredGroupsAndMissingDefault()
    {
        var services = new ServiceCollection();
        services.AddShedduellerTesting();
        await using var provider = services.BuildServiceProvider();
        var fake = provider.GetRequiredService<FakeConcurrencyGroupConfigurationReader>();
        var reader = provider.GetRequiredService<IConcurrencyGroupConfigurationReader>();
        fake.Groups["images"] = new(3, 5, 5);

        (await reader.GetConfigurationAsync("images")).ShouldBe(new(3, 5, 5));
        (await reader.GetConfigurationAsync("missing")).ShouldBe(new(null, null, 1));
        fake.Groups.Count.ShouldBe(1);
    }
}
