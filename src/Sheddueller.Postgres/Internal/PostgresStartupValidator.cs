namespace Sheddueller.Postgres.Internal;

using System.Globalization;

using Npgsql;

using Sheddueller.Runtime;

internal sealed class PostgresStartupValidator(ShedduellerPostgresOptions options) : IShedduellerStartupValidator
{
    private readonly ShedduellerPostgresOptions _options = options;
    private readonly PostgresNames _names = new(options.SchemaName);

    public async ValueTask ValidateAsync(CancellationToken cancellationToken)
    {
        PostgresOptionsValidator.Validate(this._options);

        await using var connection = await this._options.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var schemaExists = await this.SchemaExistsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!schemaExists)
        {
            throw new InvalidOperationException($"PostgreSQL schema '{this._options.SchemaName}' has not been migrated.");
        }

        var version = await this.ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version != PostgresNames.ExpectedSchemaVersion)
        {
            throw new InvalidOperationException(
              $"PostgreSQL schema '{this._options.SchemaName}' version {version?.ToString(CultureInfo.InvariantCulture) ?? "<missing>"} does not match provider version {PostgresNames.ExpectedSchemaVersion}.");
        }
    }

    private async ValueTask<bool> SchemaExistsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          "select exists (select 1 from information_schema.schemata where schema_name = @schema_name);");
        command.Parameters.AddWithValue("schema_name", this._options.SchemaName);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private async ValueTask<int?> ReadSchemaVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = PostgresCommands.Create(
              connection,
              transaction: null,
              $"select schema_version from {this._names.SchemaInfo} where singleton_id = 1;");
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }
}
