namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using NpgsqlTypes;

using Sheddueller.Enqueueing;

internal static class PostgresJobTags
{
    public static async ValueTask ReplaceJobTagsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        IReadOnlyList<JobTag>? tags,
        CancellationToken cancellationToken)
    {
        var normalized = SubmissionValidator.NormalizeJobTags(tags);
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"delete from {context.Names.JobTags} where job_id = @job_id;",
          command => command.Parameters.AddWithValue("job_id", jobId),
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
          insert into {context.Names.JobTags} (job_id, ordinal, name, value)
          select @job_id, tag.ordinal, tag.name, tag.value
          from unnest(@ordinals::integer[], @names::text[], @values::text[]) as tag(ordinal, name, value)
          on conflict (job_id, name, value) do nothing;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_id", jobId);
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
}
