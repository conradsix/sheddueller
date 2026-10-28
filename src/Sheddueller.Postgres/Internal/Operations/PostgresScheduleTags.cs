namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using NpgsqlTypes;

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
        var normalized = NormalizeTags(tags);
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"delete from {context.Names.ScheduleTags} where schedule_key = @schedule_key;",
          command => command.Parameters.AddWithValue("schedule_key", scheduleKey),
          cancellationToken)
          .ConfigureAwait(false);

        if (normalized.Count == 0)
        {
            return;
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.ScheduleTags} (schedule_key, ordinal, name, value)
          select @schedule_key, tag.ordinal, tag.name, tag.value
          from unnest(@ordinals::integer[], @names::text[], @values::text[]) as tag(ordinal, name, value)
          on conflict (schedule_key, name, value) do nothing;
          """,
          command =>
          {
              command.Parameters.AddWithValue("schedule_key", scheduleKey);
              command.Parameters.Add("ordinals", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                Enumerable.Range(0, normalized.Count).ToArray();
              command.Parameters.Add("names", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalized.Select(static tag => tag.Name).ToArray();
              command.Parameters.Add("values", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalized.Select(static tag => tag.Value).ToArray();
          },
          cancellationToken)
          .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the tags of each schedule, in tag order, with one query. A schedule without tags has no entry.
    /// </summary>
    public static async ValueTask<IReadOnlyDictionary<string, List<JobTag>>> ReadScheduleTagsByKeyAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<string> scheduleKeys,
        CancellationToken cancellationToken)
    {
        var tagsByKey = new Dictionary<string, List<JobTag>>(StringComparer.Ordinal);
        if (scheduleKeys.Count == 0)
        {
            return tagsByKey;
        }

        await using var command = PostgresCommands.Create(
          connection,
          transaction,
          $"""
          select schedule_key, name, value
          from {context.Names.ScheduleTags}
          where schedule_key = any(@schedule_keys)
          order by schedule_key asc, ordinal asc, name asc, value asc;
          """);
        command.Parameters.Add("schedule_keys", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = scheduleKeys.ToArray();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var scheduleKey = reader.GetString(0);
            if (!tagsByKey.TryGetValue(scheduleKey, out var tags))
            {
                tags = [];
                tagsByKey.Add(scheduleKey, tags);
            }

            tags.Add(new JobTag(reader.GetString(1), reader.GetString(2)));
        }

        return tagsByKey;
    }

    private static List<JobTag> NormalizeTags(IReadOnlyList<JobTag>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<JobTag>();
        var normalized = new List<JobTag>(tags.Count);
        foreach (var tag in tags)
        {
            ArgumentNullException.ThrowIfNull(tag);
            var name = tag.Name.Trim();
            var value = tag.Value.Trim();
            if (name.Length == 0)
            {
                throw new ArgumentException("Schedule tag names must be non-empty after trimming.", nameof(tags));
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("Schedule tag values must be non-empty after trimming.", nameof(tags));
            }

            var normalizedTag = new JobTag(name, value);
            if (seen.Add(normalizedTag))
            {
                normalized.Add(normalizedTag);
            }
        }

        return normalized;
    }
}
