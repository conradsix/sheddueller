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
      => JobTagNormalizer.Normalize(tags, "Job");

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
