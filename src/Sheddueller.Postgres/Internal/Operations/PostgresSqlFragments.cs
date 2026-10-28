namespace Sheddueller.Postgres.Internal.Operations;

/// <summary>
/// SQL predicates shared by the claim query and the inspection reads.
/// </summary>
/// <remarks>
/// The claim query is the authority on which jobs a worker can claim; inspection builds the same predicates from
/// these fragments so it reports exactly what a worker would do. Each fragment takes the table aliases of the
/// enclosing query, and the subqueries use their own aliases. The time arguments are SQL expressions:
/// <see cref="TransactionNow" /> for a consistent snapshot, or <see cref="ClockNow" /> where the claim must see rate
/// permits that became available during its transaction.
/// </remarks>
internal static class PostgresSqlFragments
{
    /// <summary>
    /// The start of the current transaction, the same for every statement in it.
    /// </summary>
    public const string TransactionNow = "transaction_timestamp()";

    /// <summary>
    /// The current wall-clock time, which advances during a transaction.
    /// </summary>
    public const string ClockNow = "clock_timestamp()";

    /// <summary>
    /// The job states that never change again, as a SQL list for <c>in</c> and <c>not in</c>.
    /// </summary>
    public const string TerminalStates = "('Completed', 'Failed', 'Canceled')";

    /// <summary>
    /// The order a worker claims jobs in: highest priority first, then oldest.
    /// </summary>
    public static string ClaimOrder(string job)
      => $"{job}.priority desc, {job}.enqueue_sequence asc";

    /// <summary>
    /// The job is queued and its not-before time, if any, has passed.
    /// </summary>
    public static string JobIsDue(string job, string now)
      => $"{job}.state = 'Queued' and ({job}.not_before_utc is null or {job}.not_before_utc <= {now})";

    /// <summary>
    /// The job has a prerequisite job that hasn't reached a terminal state.
    /// </summary>
    public static string HasUnfinishedPrerequisites(PostgresNames names, string job)
      => $"""
         exists (
             select 1
             from {names.JobDependencies} dependency
             join {names.Jobs} prerequisite on prerequisite.job_id = dependency.prerequisite_job_id
             where dependency.job_id = {job}.job_id
               and prerequisite.state not in {TerminalStates}
         )
         """;

    /// <summary>
    /// The concurrency group has no free slot. False when the group has no row yet: a group gets its row on its first
    /// claim, so it has nothing in use.
    /// </summary>
    public static string GroupIsSaturated(string group)
      => $"coalesce({group}.in_use_count >= {group}.effective_limit, false)";

    /// <summary>
    /// The concurrency group's rate limit has no permit available at <paramref name="now" />. False when the group has
    /// no rate limit or no row yet.
    /// </summary>
    public static string GroupIsRateLimited(string group, string now)
      => $"coalesce({group}.effective_rate_permit_count is not null and {group}.rate_theoretical_arrival_at_utc > {now}, false)";

    /// <summary>
    /// One of the job's concurrency groups is saturated or rate-limited.
    /// </summary>
    public static string HasBlockingGroup(PostgresNames names, string job, string rateNow)
      => $"""
         exists (
             select 1
             from {names.JobConcurrencyGroups} job_group
             join {names.ConcurrencyGroups} concurrency_group on concurrency_group.group_key = job_group.group_key
             where job_group.job_id = {job}.job_id
               and ({GroupIsSaturated("concurrency_group")} or {GroupIsRateLimited("concurrency_group", rateNow)})
         )
         """;

    /// <summary>
    /// A worker would claim the job: it is due, its prerequisites are terminal and none of its groups block it.
    /// </summary>
    /// <remarks>
    /// The leading <c>state = 'Queued'</c> lets the claim query scan <c>idx_jobs_claim_scan</c> in claim order.
    /// </remarks>
    public static string JobIsClaimable(PostgresNames names, string job, string rateNow)
      => $"""
         {JobIsDue(job, TransactionNow)}
           and not {HasUnfinishedPrerequisites(names, job)}
           and not {HasBlockingGroup(names, job, rateNow)}
         """;

    /// <summary>
    /// When the job reached its current terminal state, or null for a job that isn't terminal.
    /// </summary>
    /// <remarks>
    /// Chooses the timestamp by state: a job keeps <c>failed_at_utc</c> from a failed attempt after it is retried, so
    /// it can't be told apart by which timestamp is set.
    /// </remarks>
    public static string TerminalAt(string job)
      => $"""
         case {job}.state
             when 'Completed' then {job}.completed_at_utc
             when 'Failed' then {job}.failed_at_utc
             when 'Canceled' then {job}.canceled_at_utc
         end
         """;

    /// <summary>
    /// The job reached its current terminal state before the cutoff for that state. A null cutoff matches no job.
    /// </summary>
    /// <remarks>
    /// One branch per state, each a range on that state's timestamp, so the planner can use the
    /// <c>idx_jobs_inspection_completed</c>, <c>_failed</c> and <c>_canceled</c> indexes.
    /// </remarks>
    public static string TerminalBefore(string job, string completedBefore, string failedBefore, string canceledBefore)
      => $"""
         (
             ({job}.state = 'Completed' and {job}.completed_at_utc < {completedBefore})
             or ({job}.state = 'Failed' and {job}.failed_at_utc < {failedBefore})
             or ({job}.state = 'Canceled' and {job}.canceled_at_utc < {canceledBefore})
         )
         """;
}
