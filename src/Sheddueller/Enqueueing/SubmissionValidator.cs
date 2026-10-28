namespace Sheddueller.Enqueueing;

internal static class SubmissionValidator
{
    public static IReadOnlyList<string> NormalizeConcurrencyGroupKeys(IReadOnlyList<string>? groupKeys)
    {
        if (groupKeys is null || groupKeys.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(groupKeys.Count);

        foreach (var groupKey in groupKeys)
        {
            ValidateConcurrencyGroupKey(groupKey);

            if (seen.Add(groupKey))
            {
                normalized.Add(groupKey);
            }
        }

        return normalized;
    }

    public static IReadOnlyList<JobTag> NormalizeJobTags(IReadOnlyList<JobTag>? tags)
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
                throw new ArgumentException("Job tag names must be non-empty after trimming.", nameof(tags));
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("Job tag values must be non-empty after trimming.", nameof(tags));
            }

            var normalizedTag = new JobTag(name, value);
            if (seen.Add(normalizedTag))
            {
                normalized.Add(normalizedTag);
            }
        }

        return normalized;
    }

    public static void ValidateIdempotency(JobSubmission? submission)
    {
        if (submission is null)
        {
            return;
        }

        if (!Enum.IsDefined(submission.IdempotencyKind))
        {
            throw new ArgumentOutOfRangeException(nameof(submission), submission.IdempotencyKind, "Job idempotency kind is not supported.");
        }

        if (submission.IdempotencyKind != JobIdempotencyKind.None && submission.NotBeforeUtc is not null)
        {
            throw new ArgumentException("Idempotent jobs cannot be delayed with NotBeforeUtc.", nameof(submission));
        }
    }

    /// <summary>
    /// Validates an <see cref="IJobEnqueuer.EnqueueManyAsync"/> batch as a whole: every item is present once, every
    /// prerequisite is in the batch, the dependency graph is acyclic, and a batch with dependencies uses no idempotency.
    /// Each item's work and submission are validated when it is prepared.
    /// </summary>
    public static void ValidateBatch(IReadOnlyList<JobEnqueueItem> jobs)
    {
        var submitted = new HashSet<JobEnqueueItem>(ReferenceEqualityComparer.Instance);
        foreach (var job in jobs)
        {
            ArgumentNullException.ThrowIfNull(job, nameof(jobs));
            if (!submitted.Add(job))
            {
                throw new ArgumentException("A job enqueue item cannot appear more than once in a batch.", nameof(jobs));
            }
        }

        foreach (var job in jobs)
        {
            foreach (var prerequisite in job.Prerequisites)
            {
                if (!submitted.Contains(prerequisite))
                {
                    throw new ArgumentException("Every prerequisite job must be included in the same batch.", nameof(jobs));
                }
            }
        }

        var visiting = new HashSet<JobEnqueueItem>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<JobEnqueueItem>(ReferenceEqualityComparer.Instance);
        foreach (var job in jobs)
        {
            Visit(job, visiting, visited);
        }

        var hasDependencies = jobs.Any(static job => job.Prerequisites.Count > 0);
        if (hasDependencies && jobs.Any(static job => job.Submission?.IdempotencyKind is not null and not JobIdempotencyKind.None))
        {
            throw new ArgumentException("Jobs in a dependency graph cannot use idempotency.", nameof(jobs));
        }

        static void Visit(
            JobEnqueueItem job,
            HashSet<JobEnqueueItem> visiting,
            HashSet<JobEnqueueItem> visited)
        {
            if (visited.Contains(job))
            {
                return;
            }

            if (!visiting.Add(job))
            {
                throw new ArgumentException("Job dependency graphs cannot contain cycles.", nameof(jobs));
            }

            foreach (var prerequisite in job.Prerequisites)
            {
                Visit(prerequisite, visiting, visited);
            }

            visiting.Remove(job);
            visited.Add(job);
        }
    }

    public static void ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (idempotencyKey is not null && idempotencyKey.Length == 0)
        {
            throw new ArgumentException("Idempotency keys must be non-empty strings.", nameof(idempotencyKey));
        }
    }

    public static void ValidateConcurrencyGroupKey(string? groupKey)
    {
        if (string.IsNullOrEmpty(groupKey))
        {
            throw new ArgumentException("Concurrency group keys must be non-empty strings.", nameof(groupKey));
        }
    }

    public static void ValidateScheduleKey(string? scheduleKey)
    {
        if (string.IsNullOrEmpty(scheduleKey))
        {
            throw new ArgumentException("Recurring schedule keys must be non-empty strings.", nameof(scheduleKey));
        }
    }

    public static void ValidateRetryPolicy(RetryPolicy? retryPolicy)
    {
        if (retryPolicy is null)
        {
            return;
        }

        if (retryPolicy.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retryPolicy), retryPolicy.MaxAttempts, "Retry max attempts must be greater than or equal to 1.");
        }

        if (!Enum.IsDefined(retryPolicy.BackoffKind))
        {
            throw new ArgumentOutOfRangeException(nameof(retryPolicy), retryPolicy.BackoffKind, "Retry backoff kind is not supported.");
        }

        if (retryPolicy.BaseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryPolicy), retryPolicy.BaseDelay, "Retry base delay must be positive.");
        }

        if (retryPolicy.MaxDelay is { } maxDelay && maxDelay < retryPolicy.BaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(retryPolicy), maxDelay, "Retry max delay must be greater than or equal to the base delay.");
        }
    }

    public static (int MaxAttempts, RetryBackoffKind? BackoffKind, TimeSpan? BaseDelay, TimeSpan? MaxDelay) NormalizeRetryPolicy(
        RetryPolicy? retryPolicy)
    {
        ValidateRetryPolicy(retryPolicy);

        if (retryPolicy is not { MaxAttempts: > 1 })
        {
            return (1, null, null, null);
        }

        return (retryPolicy.MaxAttempts, retryPolicy.BackoffKind, retryPolicy.BaseDelay, retryPolicy.MaxDelay);
    }
}
