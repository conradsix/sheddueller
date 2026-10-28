namespace Sheddueller.Postgres.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Sheddueller.Runtime;

using Shouldly;

public sealed class PostgresStartupValidationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StartupValidation_ClientModeMissingSchema_DoesNotRegisterHostedServices()
    {
        var schemaName = "sheddueller_" + Guid.NewGuid().ToString("N");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSheddueller(sheddueller => sheddueller.UsePostgres(options =>
        {
            options.DataSource = fixture.DataSource;
            options.SchemaName = schemaName;
        }));
        using var host = builder.Build();

        host.Services.GetServices<IHostedService>().ShouldBeEmpty();
        host.Services.GetServices<IShedduellerJobEventListener>().Count().ShouldBe(1);
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task StartupValidation_DashboardModeMissingSchema_FailsStart()
    {
        var schemaName = "sheddueller_" + Guid.NewGuid().ToString("N");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSheddueller(sheddueller => sheddueller.UsePostgres(options =>
        {
            options.DataSource = fixture.DataSource;
            options.SchemaName = schemaName;
        }));
        builder.Services.AddShedduellerDashboard();
        using var host = builder.Build();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());

        exception.Message.ShouldContain($"PostgreSQL schema '{schemaName}' has not been migrated.");
    }

    [Fact]
    public async Task StartupValidation_WorkerModeMissingSchema_FailsBeforeWorkerStarts()
    {
        var schemaName = "sheddueller_" + Guid.NewGuid().ToString("N");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddShedduellerWorker(sheddueller => sheddueller.UsePostgres(options =>
        {
            options.DataSource = fixture.DataSource;
            options.SchemaName = schemaName;
        }));
        using var host = builder.Build();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());

        exception.Message.ShouldContain($"PostgreSQL schema '{schemaName}' has not been migrated.");
    }
}
