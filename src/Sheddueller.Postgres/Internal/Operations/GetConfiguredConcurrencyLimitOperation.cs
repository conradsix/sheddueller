namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

internal static class GetConfiguredConcurrencyLimitOperation
{
    public static async ValueTask<int?> ExecuteAsync(
        PostgresOperationContext context,
        string groupKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"select configured_limit from {context.Names.ConcurrencyGroups} where group_key = @group_key;");
        command.Parameters.AddWithValue("group_key", groupKey);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }
}
