namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Sheddueller.Inspection.Jobs;

internal static class PostgresJobInspectionQuery
{
    private const string OperationalContinuationTokenPrefix = "op:";

    public static string CreateWhereClause(List<string> conditions)
      => conditions.Count == 0 ? string.Empty : $"where {string.Join(" and ", conditions)}";

    public static string OperationalOrderBySql(PostgresOperationContext context)
      => $"""
         case job.state
             when 'Claimed' then 0
             when 'Queued' then 1
             else 2
         end asc,
         case when job.state = 'Claimed' then job.claimed_at_utc end desc nulls last,
         case when job.state = 'Claimed' then job.enqueue_sequence end desc,
         case
             when job.state <> 'Queued' then 0
             when job.not_before_utc is not null
                  and job.not_before_utc > transaction_timestamp()
                  and job.failed_at_utc is not null then 2
             when job.not_before_utc is not null
                  and job.not_before_utc > transaction_timestamp() then 3
             when exists (
                 select 1
                 from {context.Names.JobConcurrencyGroups} job_group
                 left join {context.Names.ConcurrencyGroups} concurrency_group on concurrency_group.group_key = job_group.group_key
                 where job_group.job_id = job.job_id
                   and coalesce(concurrency_group.in_use_count, 0) >= coalesce(concurrency_group.effective_limit, 1)
             ) then 1
             else 0
         end asc,
         case when job.state = 'Queued' then job.priority end desc,
         case when job.state = 'Queued' then job.enqueue_sequence end asc,
         case when job.state in ('Completed', 'Failed', 'Canceled') then coalesce(job.completed_at_utc, job.failed_at_utc, job.canceled_at_utc) end desc nulls last,
         job.enqueue_sequence desc
         """;

    public static void ValidateJobQuery(JobInspectionQuery query)
    {
        if (query.PageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.PageSize, "Job inspection query page size must be positive.");
        }

        if (query.States is not null)
        {
            foreach (var state in query.States)
            {
                if (!Enum.IsDefined(state))
                {
                    throw new ArgumentOutOfRangeException(nameof(query), state, "Job state is not supported.");
                }
            }
        }

        if (!Enum.IsDefined(query.Sort))
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Job inspection sort is not supported.");
        }

        _ = query.Sort switch
        {
            JobInspectionSort.NewestFirst => DecodeNewestContinuationToken(query.ContinuationToken) ?? 0,
            JobInspectionSort.Operational => DecodeOperationalContinuationToken(query.ContinuationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Job inspection sort is not supported."),
        };
    }

    public static string? NormalizeContains(string? value)
      => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string CreateContainsPattern(string value)
      => string.Concat("%", EscapeLikePattern(value), "%");

    public static string EscapeLikePattern(string value)
      => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    public static string EncodeOperationalContinuationToken(long offset)
      => string.Concat(OperationalContinuationTokenPrefix, offset.ToString(CultureInfo.InvariantCulture));

    public static long DecodeOperationalContinuationToken(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return 0;
        }

        var offsetToken = continuationToken.StartsWith(OperationalContinuationTokenPrefix, StringComparison.Ordinal)
          ? continuationToken[OperationalContinuationTokenPrefix.Length..]
          : continuationToken;

        if (!long.TryParse(offsetToken, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
        {
            throw new ArgumentException("Job inspection continuation token is invalid.", nameof(continuationToken));
        }

        return offset;
    }

    public static long? DecodeNewestContinuationToken(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return null;
        }

        if (!long.TryParse(continuationToken, NumberStyles.None, CultureInfo.InvariantCulture, out var enqueueSequence) || enqueueSequence <= 0)
        {
            throw new ArgumentException("Job inspection continuation token is invalid.", nameof(continuationToken));
        }

        return enqueueSequence;
    }
}
