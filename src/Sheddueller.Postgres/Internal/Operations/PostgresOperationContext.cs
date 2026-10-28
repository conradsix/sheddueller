namespace Sheddueller.Postgres.Internal.Operations;

using System.Data;

using Npgsql;

internal sealed class PostgresOperationContext(ShedduellerPostgresOptions options)
{
    public ShedduellerPostgresOptions Options { get; } = options;

    public PostgresNames Names { get; } = new(options.SchemaName);

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
      => this.Options.DataSource.OpenConnectionAsync(cancellationToken);

    public async ValueTask NotifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => await ExecuteCountAsync(
        connection,
        transaction,
        "select pg_notify(@channel, @payload);",
        command =>
        {
            command.Parameters.AddWithValue("channel", PostgresNames.WakeupChannel);
            command.Parameters.AddWithValue("payload", this.Options.SchemaName);
        },
        cancellationToken)
      .ConfigureAwait(false);

    public static object ToDbValue(object? value)
      => value ?? DBNull.Value;

    /// <summary>
    /// Begins a read-only REPEATABLE READ transaction, so every statement of a multi-statement read sees the same
    /// snapshot and the same <c>transaction_timestamp()</c>.
    /// </summary>
    public static async ValueTask<NpgsqlTransaction> BeginReadSnapshotAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteCountAsync(connection, transaction, "set transaction read only;", static _ => { }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return transaction;
    }

    /// <summary>
    /// Reads <c>transaction_timestamp()</c>: the start of <paramref name="transaction" />, or of the connection's
    /// current transaction when it is null, which is the "now" every other statement in that transaction sees.
    /// </summary>
    public static async ValueTask<DateTimeOffset> ReadTransactionTimestampAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(connection, transaction, "select transaction_timestamp();");
        return PostgresConversion.ToDateTimeOffset(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return transaction_timestamp()."));
    }

    public static async ValueTask<int> ExecuteCountAsync(
        NpgsqlConnection connection,
        string commandText,
        Action<NpgsqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(connection, transaction: null, commandText);
        configure(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<int> ExecuteCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText,
        Action<NpgsqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(connection, transaction, commandText);
        configure(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
