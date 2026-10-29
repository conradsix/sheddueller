namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller.Enqueueing;

internal sealed class PostgresConcurrencyGroupConfigurationReader(PostgresOperationContext context) : IConcurrencyGroupConfigurationReader
{
    public async ValueTask<ConcurrencyGroupConfiguration> GetConfigurationAsync(string groupKey, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = PostgresCommands.Create(connection, transaction: null,
          $"select default_limit, configured_limit, effective_limit from {context.Names.ConcurrencyGroups} where group_key = @group_key;");
        command.Parameters.AddWithValue("group_key", groupKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
          ? new(reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetInt32(2))
          : new(null, null, 1);
    }
}
