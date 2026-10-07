namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using Sheddueller.Enqueueing;

internal static class PostgresScheduleTags
{
    public static async ValueTask ReplaceScheduleTagsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scheduleKey,
        IReadOnlyList<JobTag>? tags,
        CancellationToken cancellationToken)
    {
        var normalized = JobTagNormalizer.Normalize(tags, "Schedule");
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"delete from {context.Names.ScheduleTags} where schedule_key = @schedule_key;",
          command => command.Parameters.AddWithValue("schedule_key", scheduleKey),
          cancellationToken)
          .ConfigureAwait(false);

        for (var i = 0; i < normalized.Count; i++)
        {
            var tag = normalized[i];
            await PostgresOperationContext.ExecuteCountAsync(
              connection,
              transaction,
              $"""
              insert into {context.Names.ScheduleTags} (schedule_key, ordinal, name, value)
              values (@schedule_key, @ordinal, @name, @value)
              on conflict (schedule_key, name, value) do nothing;
              """,
              command =>
              {
                  command.Parameters.AddWithValue("schedule_key", scheduleKey);
                  command.Parameters.AddWithValue("ordinal", i);
                  command.Parameters.AddWithValue("name", tag.Name);
                  command.Parameters.AddWithValue("value", tag.Value);
              },
              cancellationToken)
              .ConfigureAwait(false);
        }
    }

    public static async ValueTask<IReadOnlyList<JobTag>> ReadScheduleTagsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select name, value
          from {context.Names.ScheduleTags}
          where schedule_key = @schedule_key
          order by ordinal asc, name asc, value asc;
          """;
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        var tags = new List<JobTag>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tags.Add(new JobTag(reader.GetString(0), reader.GetString(1)));
        }

        return tags;
    }
}
