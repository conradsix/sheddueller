namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

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

        for (var i = 0; i < normalized.Count; i++)
        {
            var tag = normalized[i];
            await PostgresOperationContext.ExecuteCountAsync(
              connection,
              transaction,
              $"""
              insert into {context.Names.JobTags} (job_id, ordinal, name, value)
              values (@job_id, @ordinal, @name, @value)
              on conflict (job_id, name, value) do nothing;
              """,
              command =>
              {
                  command.Parameters.AddWithValue("job_id", jobId);
                  command.Parameters.AddWithValue("ordinal", i);
                  command.Parameters.AddWithValue("name", tag.Name);
                  command.Parameters.AddWithValue("value", tag.Value);
              },
              cancellationToken)
              .ConfigureAwait(false);
        }
    }
}
