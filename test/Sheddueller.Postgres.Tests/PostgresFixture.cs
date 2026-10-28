// One PostgreSQL container serves the whole test assembly: every test works in a schema of its own, so the tests
// share the server without seeing each other's data.
[assembly: AssemblyFixture(typeof(Sheddueller.Postgres.Tests.PostgresFixture))]

namespace Sheddueller.Postgres.Tests;

using Npgsql;

using Testcontainers.PostgreSql;

public sealed class PostgresFixture : IAsyncLifetime
{
    // Every test class shares this one server and runs in parallel with the others, so the server allows more
    // connections than PostgreSQL's default of 100.
    private const string MaxConnections = "max_connections=500";

    private PostgreSqlContainer? _container;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var image = Environment.GetEnvironmentVariable("SHEDDUELLER_POSTGRES_IMAGE");
        if (string.IsNullOrWhiteSpace(image))
        {
            image = "postgres:14";
        }

        this._container = new PostgreSqlBuilder(image)
          .WithDatabase("sheddueller_tests")
          .WithUsername("postgres")
          .WithPassword("postgres")
          .WithCommand("-c", MaxConnections)
          .Build();

        await this._container.StartAsync();
        this.ConnectionString = this._container.GetConnectionString();
        this.DataSource = NpgsqlDataSource.Create(this.ConnectionString);

        // Each migration creates pg_trgm if it is missing. Extensions are database-wide, and concurrent CREATE
        // EXTENSION IF NOT EXISTS statements race on the catalog, so the extension exists before any test migrates.
        await using var command = this.DataSource.CreateCommand("create extension if not exists pg_trgm;");
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (this.DataSource is not null)
        {
            await this.DataSource.DisposeAsync();
        }

        if (this._container is not null)
        {
            await this._container.DisposeAsync();
        }
    }
}
