namespace Sheddueller.Runtime;

using System.Linq.Expressions;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller.Enqueueing;
using Sheddueller.Logging;
using Sheddueller.Scheduling;
using Sheddueller.Serialization;
using Sheddueller.Storage;

internal sealed class RecurringScheduleManager(
    IJobStore store,
    IJobPayloadSerializer serializer,
    IOptions<ShedduellerOptions> options,
    TimeProvider timeProvider,
    IShedduellerWakeSignal wakeSignal,
    ILogger<RecurringScheduleManager> logger) : IRecurringScheduleManager
{
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
        string scheduleKey,
        string cronExpression,
        Expression<Func<CancellationToken, Task>> work,
        RecurringScheduleOptions? options = null,
        RecurringScheduleUpdateOptions? updateOptions = null,
        CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, serviceType: null, work, options, updateOptions, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
        string scheduleKey,
        string cronExpression,
        Expression<Func<CancellationToken, Task>> work,
        RecurringScheduleOptions? options,
        CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
        string scheduleKey,
        string cronExpression,
        Expression<Func<CancellationToken, ValueTask>> work,
        RecurringScheduleOptions? options = null,
        RecurringScheduleUpdateOptions? updateOptions = null,
        CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, serviceType: null, work, options, updateOptions, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
        string scheduleKey,
        string cronExpression,
        Expression<Func<CancellationToken, ValueTask>> work,
        RecurringScheduleOptions? options,
        CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
        string scheduleKey,
        string cronExpression,
        Expression<Func<CancellationToken, IProgress<decimal>, Task>> work,
        RecurringScheduleOptions? options = null,
        RecurringScheduleUpdateOptions? updateOptions = null,
        CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, serviceType: null, work, options, updateOptions, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
        string scheduleKey,
        string cronExpression,
        Expression<Func<CancellationToken, IProgress<decimal>, Task>> work,
        RecurringScheduleOptions? options,
        CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
        string scheduleKey,
        string cronExpression,
        Expression<Func<CancellationToken, IProgress<decimal>, ValueTask>> work,
        RecurringScheduleOptions? options = null,
        RecurringScheduleUpdateOptions? updateOptions = null,
        CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, serviceType: null, work, options, updateOptions, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
        string scheduleKey,
        string cronExpression,
        Expression<Func<CancellationToken, IProgress<decimal>, ValueTask>> work,
        RecurringScheduleOptions? options,
        CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
        string scheduleKey,
        string cronExpression,
        Expression<Func<TService, CancellationToken, Task>> work,
        RecurringScheduleOptions? options = null,
        RecurringScheduleUpdateOptions? updateOptions = null,
        CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, typeof(TService), work, options, updateOptions, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
        string scheduleKey,
        string cronExpression,
        Expression<Func<TService, CancellationToken, Task>> work,
        RecurringScheduleOptions? options,
        CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
        string scheduleKey,
        string cronExpression,
        Expression<Func<TService, CancellationToken, ValueTask>> work,
        RecurringScheduleOptions? options = null,
        RecurringScheduleUpdateOptions? updateOptions = null,
        CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, typeof(TService), work, options, updateOptions, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
        string scheduleKey,
        string cronExpression,
        Expression<Func<TService, CancellationToken, ValueTask>> work,
        RecurringScheduleOptions? options,
        CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
        string scheduleKey,
        string cronExpression,
        Expression<Func<TService, CancellationToken, IProgress<decimal>, Task>> work,
        RecurringScheduleOptions? options = null,
        RecurringScheduleUpdateOptions? updateOptions = null,
        CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, typeof(TService), work, options, updateOptions, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
        string scheduleKey,
        string cronExpression,
        Expression<Func<TService, CancellationToken, IProgress<decimal>, Task>> work,
        RecurringScheduleOptions? options,
        CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
        string scheduleKey,
        string cronExpression,
        Expression<Func<TService, CancellationToken, IProgress<decimal>, ValueTask>> work,
        RecurringScheduleOptions? options = null,
        RecurringScheduleUpdateOptions? updateOptions = null,
        CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, typeof(TService), work, options, updateOptions, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
        string scheduleKey,
        string cronExpression,
        Expression<Func<TService, CancellationToken, IProgress<decimal>, ValueTask>> work,
        RecurringScheduleOptions? options,
        CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    public async ValueTask<RecurringScheduleTriggerResult> TriggerAsync(
        string scheduleKey,
        CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);

        var result = await store.TriggerRecurringScheduleAsync(
          new TriggerRecurringScheduleRequest(scheduleKey, options.Value.DefaultRetryPolicy),
          cancellationToken)
          .ConfigureAwait(false);

        if (result.Status == RecurringScheduleTriggerStatus.Enqueued)
        {
            wakeSignal.Notify();
        }

        logger.RecurringScheduleTriggered(scheduleKey, result.Status.ToString());

        return result;
    }

    public async ValueTask<bool> DeleteAsync(string scheduleKey, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        var deleted = await store.DeleteRecurringScheduleAsync(scheduleKey, cancellationToken).ConfigureAwait(false);
        logger.RecurringScheduleDeleted(scheduleKey, deleted);

        return deleted;
    }

    public async ValueTask<bool> PauseAsync(string scheduleKey, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        var paused = await store.PauseRecurringScheduleAsync(scheduleKey, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        logger.RecurringSchedulePaused(scheduleKey, paused);

        return paused;
    }

    public async ValueTask<bool> ResumeAsync(string scheduleKey, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        var resumed = await store.ResumeRecurringScheduleAsync(scheduleKey, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        logger.RecurringScheduleResumed(scheduleKey, resumed);

        return resumed;
    }

    public ValueTask<RecurringScheduleInfo?> GetAsync(string scheduleKey, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        return store.GetRecurringScheduleAsync(scheduleKey, cancellationToken);
    }

    public async IAsyncEnumerable<RecurringScheduleInfo> ListAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var schedule in await store.ListRecurringSchedulesAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return schedule;
        }
    }

    private async ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateCoreAsync(
        string scheduleKey,
        string cronExpression,
        Type? serviceType,
        LambdaExpression work,
        RecurringScheduleOptions? options,
        RecurringScheduleUpdateOptions? updateOptions,
        CancellationToken cancellationToken)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        CronSchedule.Validate(cronExpression);
        ArgumentNullException.ThrowIfNull(work);

        var parsedJob = JobExpressionParser.Parse(serviceType, work);
        var groups = SubmissionValidator.NormalizeConcurrencyGroupKeys(options?.ConcurrencyGroupKeys);
        var tags = SubmissionValidator.NormalizeJobTags(options?.Tags);
        SubmissionValidator.ValidateRetryPolicy(options?.RetryPolicy);

        var serializedArguments = await serializer
          .SerializeAsync(parsedJob.SerializableArguments, parsedJob.SerializableParameterTypes, cancellationToken)
          .ConfigureAwait(false);

        var request = new UpsertRecurringScheduleRequest(
          scheduleKey,
          cronExpression,
          TypeNameFormatter.Format(parsedJob.ServiceType),
          parsedJob.MethodName,
          parsedJob.MethodParameterTypeNames,
          serializedArguments,
          options?.Priority ?? 0,
          groups,
          options?.RetryPolicy,
          options?.OverlapMode ?? RecurringOverlapMode.Skip,
          timeProvider.GetUtcNow(),
          tags,
          parsedJob.InvocationTargetKind,
          parsedJob.MethodParameterBindings,
          updateOptions ?? RecurringScheduleUpdateOptions.Default);

        var result = await store.CreateOrUpdateRecurringScheduleAsync(request, cancellationToken).ConfigureAwait(false);
        if (result != RecurringScheduleUpsertResult.Unchanged)
        {
            wakeSignal.Notify();
        }

        logger.RecurringScheduleUpserted(scheduleKey, result.ToString());

        return result;
    }
}
