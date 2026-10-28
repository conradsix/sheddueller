namespace Sheddueller.Runtime;

using Microsoft.Extensions.Logging;

using Sheddueller.Enqueueing;
using Sheddueller.Logging;
using Sheddueller.Storage;

internal sealed class ConcurrencyGroupManager(
    IJobStore store,
    TimeProvider timeProvider,
    IShedduellerWakeSignal wakeSignal,
    ILogger<ConcurrencyGroupManager> logger) : IConcurrencyGroupManager
{
    public async ValueTask SetLimitAsync(string groupKey, int limit, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);
        ValidateLimit(limit);

        await store
          .SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest(groupKey, limit, timeProvider.GetUtcNow()), cancellationToken)
          .ConfigureAwait(false);
        wakeSignal.Notify();
        logger.ConcurrencyGroupLimitSet(groupKey, limit);
    }

    public async ValueTask SetDefaultLimitAsync(string groupKey, int limit, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);
        ValidateLimit(limit);

        await store
          .SetConcurrencyDefaultLimitAsync(new SetConcurrencyDefaultLimitRequest(groupKey, limit, timeProvider.GetUtcNow()), cancellationToken)
          .ConfigureAwait(false);
        wakeSignal.Notify();
        logger.ConcurrencyGroupDefaultLimitSet(groupKey, limit);
    }

    public async ValueTask ClearLimitOverrideAsync(string groupKey, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);

        await store
          .ClearConcurrencyLimitOverrideAsync(new ClearConcurrencyLimitOverrideRequest(groupKey, timeProvider.GetUtcNow()), cancellationToken)
          .ConfigureAwait(false);
        wakeSignal.Notify();
        logger.ConcurrencyGroupLimitOverrideCleared(groupKey);
    }

    public ValueTask<int?> GetConfiguredLimitAsync(string groupKey, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);

        return store.GetConfiguredConcurrencyLimitAsync(groupKey, cancellationToken);
    }

    public async ValueTask SetRateLimitAsync(
        string groupKey,
        ConcurrencyGroupRateLimit rateLimit,
        CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);
        ValidateRateLimit(rateLimit);

        await store
          .SetConcurrencyRateLimitAsync(new SetConcurrencyRateLimitRequest(groupKey, rateLimit, timeProvider.GetUtcNow()), cancellationToken)
          .ConfigureAwait(false);
        wakeSignal.Notify();
        logger.ConcurrencyGroupRateLimitSet(groupKey, rateLimit.PermitCount, rateLimit.Period);
    }

    public async ValueTask SetDefaultRateLimitAsync(
        string groupKey,
        ConcurrencyGroupRateLimit rateLimit,
        CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);
        ValidateRateLimit(rateLimit);

        await store
          .SetConcurrencyDefaultRateLimitAsync(
            new SetConcurrencyDefaultRateLimitRequest(groupKey, rateLimit, timeProvider.GetUtcNow()),
            cancellationToken)
          .ConfigureAwait(false);
        wakeSignal.Notify();
        logger.ConcurrencyGroupDefaultRateLimitSet(groupKey, rateLimit.PermitCount, rateLimit.Period);
    }

    public async ValueTask ClearDefaultRateLimitAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);

        await store
          .ClearConcurrencyDefaultRateLimitAsync(
            new ClearConcurrencyDefaultRateLimitRequest(groupKey, timeProvider.GetUtcNow()),
            cancellationToken)
          .ConfigureAwait(false);
        wakeSignal.Notify();
        logger.ConcurrencyGroupDefaultRateLimitCleared(groupKey);
    }

    public async ValueTask SetUnlimitedRateLimitAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);

        await store
          .SetConcurrencyUnlimitedRateLimitAsync(
            new SetConcurrencyUnlimitedRateLimitRequest(groupKey, timeProvider.GetUtcNow()),
            cancellationToken)
          .ConfigureAwait(false);
        wakeSignal.Notify();
        logger.ConcurrencyGroupUnlimitedRateLimitSet(groupKey);
    }

    public async ValueTask ClearRateLimitOverrideAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);

        await store
          .ClearConcurrencyRateLimitOverrideAsync(
            new ClearConcurrencyRateLimitOverrideRequest(groupKey, timeProvider.GetUtcNow()),
            cancellationToken)
          .ConfigureAwait(false);
        wakeSignal.Notify();
        logger.ConcurrencyGroupRateLimitOverrideCleared(groupKey);
    }

    public ValueTask<ConcurrencyGroupRateLimitOverride> GetRateLimitOverrideAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);

        return store.GetConcurrencyRateLimitOverrideAsync(groupKey, cancellationToken);
    }

    private static void ValidateLimit(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Concurrency group limits must be positive.");
        }
    }

    private static void ValidateRateLimit(ConcurrencyGroupRateLimit rateLimit)
    {
        ArgumentNullException.ThrowIfNull(rateLimit);

        if (rateLimit.PermitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
              nameof(rateLimit),
              rateLimit.PermitCount,
              "Concurrency group rate-limit permit counts must be positive.");
        }

        if (rateLimit.Period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
              nameof(rateLimit),
              rateLimit.Period,
              "Concurrency group rate-limit periods must be positive.");
        }

        if (rateLimit.Period.TotalMicroseconds / rateLimit.PermitCount < 1)
        {
            throw new ArgumentOutOfRangeException(
              nameof(rateLimit),
              rateLimit,
              "Concurrency group rate-limit emission intervals must be at least one microsecond.");
        }
    }
}
