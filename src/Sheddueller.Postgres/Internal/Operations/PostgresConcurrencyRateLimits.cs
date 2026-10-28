namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using NpgsqlTypes;

internal static class PostgresConcurrencyRateLimits
{
    public static async ValueTask UpdateAsync(
        PostgresOperationContext context,
        string groupKey,
        Func<Configuration, Configuration> update,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.ConcurrencyGroups} (group_key, configured_limit, in_use_count, updated_at_utc)
          values (@group_key, null, 0, transaction_timestamp())
          on conflict (group_key) do nothing;
          """,
          command => command.Parameters.AddWithValue("group_key", groupKey),
          cancellationToken)
          .ConfigureAwait(false);

        var current = await ReadForUpdateAsync(context, connection, transaction, groupKey, cancellationToken).ConfigureAwait(false);
        var next = update(current);
        var resetRateState = current.EffectiveRateLimit != next.EffectiveRateLimit;

        await using var command = PostgresCommands.Create(
          connection,
          transaction,
          $"""
          update {context.Names.ConcurrencyGroups}
          set rate_limit_override_enabled = @override_enabled,
              configured_rate_permit_count = @configured_permit_count,
              configured_rate_period = @configured_period,
              default_rate_permit_count = @default_permit_count,
              default_rate_period = @default_period,
              rate_theoretical_arrival_at_utc = case
                  when @reset_rate_state then null
                  else rate_theoretical_arrival_at_utc
              end,
              updated_at_utc = transaction_timestamp()
          where group_key = @group_key;
          """);
        command.Parameters.AddWithValue("group_key", groupKey);
        command.Parameters.AddWithValue("override_enabled", next.OverrideEnabled);
        AddNullableInteger(command, "configured_permit_count", next.ConfiguredRateLimit?.PermitCount);
        AddNullableInterval(command, "configured_period", next.ConfiguredRateLimit?.Period);
        AddNullableInteger(command, "default_permit_count", next.DefaultRateLimit?.PermitCount);
        AddNullableInterval(command, "default_period", next.DefaultRateLimit?.Period);
        command.Parameters.AddWithValue("reset_rate_state", resetRateState);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ConcurrencyGroupRateLimitOverride> GetOverrideAsync(
        PostgresOperationContext context,
        string groupKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select rate_limit_override_enabled, configured_rate_permit_count, configured_rate_period
          from {context.Names.ConcurrencyGroups}
          where group_key = @group_key;
          """);
        command.Parameters.AddWithValue("group_key", groupKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.GetBoolean(0))
        {
            return new ConcurrencyGroupRateLimitOverride(ConcurrencyGroupRateLimitOverrideKind.Inherit);
        }

        return reader.IsDBNull(1)
          ? new ConcurrencyGroupRateLimitOverride(ConcurrencyGroupRateLimitOverrideKind.Unlimited)
          : new ConcurrencyGroupRateLimitOverride(
            ConcurrencyGroupRateLimitOverrideKind.Limited,
            new ConcurrencyGroupRateLimit(reader.GetInt32(1), reader.GetTimeSpan(2)));
    }

    private static async ValueTask<Configuration> ReadForUpdateAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string groupKey,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction,
          $"""
          select
              rate_limit_override_enabled,
              configured_rate_permit_count,
              configured_rate_period,
              default_rate_permit_count,
              default_rate_period
          from {context.Names.ConcurrencyGroups}
          where group_key = @group_key
          for update;
          """);
        command.Parameters.AddWithValue("group_key", groupKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Concurrency group '{groupKey}' could not be initialized.");
        }

        return new Configuration(
          reader.GetBoolean(0),
          PostgresSql.ReadRateLimit(reader, 1, 2),
          PostgresSql.ReadRateLimit(reader, 3, 4));
    }

    private static void AddNullableInteger(NpgsqlCommand command, string name, int? value)
      => command.Parameters.AddWithValue(name, NpgsqlDbType.Integer, value is null ? DBNull.Value : value.Value);

    private static void AddNullableInterval(NpgsqlCommand command, string name, TimeSpan? value)
      => command.Parameters.AddWithValue(name, NpgsqlDbType.Interval, value is null ? DBNull.Value : value.Value);

    internal sealed record Configuration(
        bool OverrideEnabled,
        ConcurrencyGroupRateLimit? ConfiguredRateLimit,
        ConcurrencyGroupRateLimit? DefaultRateLimit)
    {
        public ConcurrencyGroupRateLimit? EffectiveRateLimit
          => this.OverrideEnabled ? this.ConfiguredRateLimit : this.DefaultRateLimit;
    }
}
