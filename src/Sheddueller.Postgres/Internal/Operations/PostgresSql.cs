namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

/// <summary>
/// Small helpers for building queries and reading their results.
/// </summary>
internal static class PostgresSql
{
    /// <summary>
    /// A <c>where</c> clause that requires every condition, or an empty string when there are none.
    /// </summary>
    public static string WhereClause(IReadOnlyCollection<string> conditions)
      => conditions.Count == 0 ? string.Empty : $"where {string.Join(" and ", conditions)}";

    /// <summary>
    /// Runs the command and reads the first column of every row as a <see cref="Guid" />, in row order.
    /// </summary>
    public static async ValueTask<IReadOnlyList<Guid>> ReadGuidsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var values = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetGuid(0));
        }

        return values;
    }

    /// <summary>
    /// Reads a rate limit stored as a permit count and a period, or null when the permit count is null.
    /// </summary>
    public static ConcurrencyGroupRateLimit? ReadRateLimit(
        NpgsqlDataReader reader,
        int countOrdinal,
        int periodOrdinal)
      => reader.IsDBNull(countOrdinal)
        ? null
        : new ConcurrencyGroupRateLimit(reader.GetInt32(countOrdinal), reader.GetTimeSpan(periodOrdinal));
}
