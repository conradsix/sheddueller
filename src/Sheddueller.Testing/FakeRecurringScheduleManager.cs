namespace Sheddueller.Testing;

using System.Linq.Expressions;
using System.Runtime.CompilerServices;

using Sheddueller.Enqueueing;
using Sheddueller.Scheduling;
using Sheddueller.Serialization;

/// <summary>
/// Test double for recording and matching recurring schedules submitted through <see cref="IRecurringScheduleManager"/>.
/// </summary>
public sealed class FakeRecurringScheduleManager : IRecurringScheduleManager
{
    private readonly Lock _syncRoot = new();
    private readonly IJobPayloadSerializer _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, FakeRecurringSchedule> _schedules = new(StringComparer.Ordinal);
    private readonly List<FakeTriggeredJob> _triggeredJobs = [];
    private long _nextEnqueueSequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeRecurringScheduleManager"/> class.
    /// </summary>
    public FakeRecurringScheduleManager()
      : this(new SystemTextJsonJobPayloadSerializer(), TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeRecurringScheduleManager"/> class.
    /// </summary>
    /// <param name="serializer">The serializer used to compare captured recurring schedule arguments.</param>
    public FakeRecurringScheduleManager(IJobPayloadSerializer serializer)
      : this(serializer, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeRecurringScheduleManager"/> class.
    /// </summary>
    /// <param name="serializer">The serializer used to compare captured recurring schedule arguments.</param>
    /// <param name="timeProvider">The time provider used to compute next fire times.</param>
    public FakeRecurringScheduleManager(
      IJobPayloadSerializer serializer,
      TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this._serializer = serializer;
        this._timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets a snapshot of current recurring schedules recorded by this fake.
    /// </summary>
    public IReadOnlyList<FakeRecurringSchedule> Schedules
    {
        get
        {
            lock (this._syncRoot)
            {
                return Array.AsReadOnly([.. this._schedules.Values.OrderBy(schedule => schedule.ScheduleKey, StringComparer.Ordinal)]);
            }
        }
    }

    /// <summary>
    /// Gets a snapshot of jobs created by manual schedule triggers.
    /// </summary>
    public IReadOnlyList<FakeTriggeredJob> TriggeredJobs
    {
        get
        {
            lock (this._syncRoot)
            {
                return Array.AsReadOnly([.. this._triggeredJobs]);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
      string scheduleKey,
      string cronExpression,
      Expression<Func<CancellationToken, Task>> work,
      RecurringScheduleOptions? options = null,
      RecurringScheduleUpdateOptions? updateOptions = null,
      CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, JobExpressionParser.Parse(work), options, updateOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
      string scheduleKey,
      string cronExpression,
      Expression<Func<CancellationToken, Task>> work,
      RecurringScheduleOptions? options,
      CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
      string scheduleKey,
      string cronExpression,
      Expression<Func<CancellationToken, ValueTask>> work,
      RecurringScheduleOptions? options = null,
      RecurringScheduleUpdateOptions? updateOptions = null,
      CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, JobExpressionParser.Parse(work), options, updateOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
      string scheduleKey,
      string cronExpression,
      Expression<Func<CancellationToken, ValueTask>> work,
      RecurringScheduleOptions? options,
      CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
      string scheduleKey,
      string cronExpression,
      Expression<Func<CancellationToken, IProgress<decimal>, Task>> work,
      RecurringScheduleOptions? options = null,
      RecurringScheduleUpdateOptions? updateOptions = null,
      CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, JobExpressionParser.Parse(work), options, updateOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
      string scheduleKey,
      string cronExpression,
      Expression<Func<CancellationToken, IProgress<decimal>, Task>> work,
      RecurringScheduleOptions? options,
      CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
      string scheduleKey,
      string cronExpression,
      Expression<Func<CancellationToken, IProgress<decimal>, ValueTask>> work,
      RecurringScheduleOptions? options = null,
      RecurringScheduleUpdateOptions? updateOptions = null,
      CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, JobExpressionParser.Parse(work), options, updateOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync(
      string scheduleKey,
      string cronExpression,
      Expression<Func<CancellationToken, IProgress<decimal>, ValueTask>> work,
      RecurringScheduleOptions? options,
      CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
      string scheduleKey,
      string cronExpression,
      Expression<Func<TService, CancellationToken, Task>> work,
      RecurringScheduleOptions? options = null,
      RecurringScheduleUpdateOptions? updateOptions = null,
      CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, JobExpressionParser.Parse(work), options, updateOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
      string scheduleKey,
      string cronExpression,
      Expression<Func<TService, CancellationToken, Task>> work,
      RecurringScheduleOptions? options,
      CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
      string scheduleKey,
      string cronExpression,
      Expression<Func<TService, CancellationToken, IProgress<decimal>, Task>> work,
      RecurringScheduleOptions? options = null,
      RecurringScheduleUpdateOptions? updateOptions = null,
      CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, JobExpressionParser.Parse(work), options, updateOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
      string scheduleKey,
      string cronExpression,
      Expression<Func<TService, CancellationToken, IProgress<decimal>, Task>> work,
      RecurringScheduleOptions? options,
      CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
      string scheduleKey,
      string cronExpression,
      Expression<Func<TService, CancellationToken, IProgress<decimal>, ValueTask>> work,
      RecurringScheduleOptions? options = null,
      RecurringScheduleUpdateOptions? updateOptions = null,
      CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, JobExpressionParser.Parse(work), options, updateOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
      string scheduleKey,
      string cronExpression,
      Expression<Func<TService, CancellationToken, IProgress<decimal>, ValueTask>> work,
      RecurringScheduleOptions? options,
      CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
      string scheduleKey,
      string cronExpression,
      Expression<Func<TService, CancellationToken, ValueTask>> work,
      RecurringScheduleOptions? options = null,
      RecurringScheduleUpdateOptions? updateOptions = null,
      CancellationToken cancellationToken = default)
      => this.CreateOrUpdateCoreAsync(scheduleKey, cronExpression, JobExpressionParser.Parse(work), options, updateOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateAsync<TService>(
      string scheduleKey,
      string cronExpression,
      Expression<Func<TService, CancellationToken, ValueTask>> work,
      RecurringScheduleOptions? options,
      CancellationToken cancellationToken)
      => this.CreateOrUpdateAsync(scheduleKey, cronExpression, work, options, updateOptions: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecurringScheduleTriggerResult> TriggerAsync(
      string scheduleKey,
      CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this._syncRoot)
        {
            if (!this._schedules.TryGetValue(scheduleKey, out var schedule))
            {
                return ValueTask.FromResult(new RecurringScheduleTriggerResult(RecurringScheduleTriggerStatus.NotFound));
            }

            if (schedule.OverlapMode == RecurringOverlapMode.Skip
              && this._triggeredJobs.Any(job => string.Equals(job.SourceScheduleKey, scheduleKey, StringComparison.Ordinal)))
            {
                return ValueTask.FromResult(new RecurringScheduleTriggerResult(RecurringScheduleTriggerStatus.SkippedActiveOccurrence));
            }

            var triggeredJob = this.CreateTriggeredJob(schedule);
            this._triggeredJobs.Add(triggeredJob);

            return ValueTask.FromResult(new RecurringScheduleTriggerResult(
              RecurringScheduleTriggerStatus.Enqueued,
              triggeredJob.JobId,
              triggeredJob.EnqueueSequence));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(
      string scheduleKey,
      CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this._syncRoot)
        {
            return ValueTask.FromResult(this._schedules.Remove(scheduleKey));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> PauseAsync(
      string scheduleKey,
      CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this._syncRoot)
        {
            if (!this._schedules.TryGetValue(scheduleKey, out var schedule))
            {
                return ValueTask.FromResult(false);
            }

            this._schedules[scheduleKey] = schedule.WithPaused(isPaused: true, nextFireAtUtc: null);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ResumeAsync(
      string scheduleKey,
      CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this._syncRoot)
        {
            if (!this._schedules.TryGetValue(scheduleKey, out var schedule))
            {
                return ValueTask.FromResult(false);
            }

            this._schedules[scheduleKey] = schedule.WithPaused(isPaused: false, this.GetNextFireAtUtc(schedule.CronExpression));
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<RecurringScheduleInfo?> GetAsync(
      string scheduleKey,
      CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this._syncRoot)
        {
            return ValueTask.FromResult(this._schedules.TryGetValue(scheduleKey, out var schedule) ? schedule.ToInfo() : null);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecurringScheduleInfo> ListAsync(
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        FakeRecurringSchedule[] schedules;

        lock (this._syncRoot)
        {
            schedules = [.. this._schedules.Values.OrderBy(schedule => schedule.ScheduleKey, StringComparer.Ordinal)];
        }

        foreach (var schedule in schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return schedule.ToInfo();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds recorded recurring schedules that match a Task-returning job method call.
    /// </summary>
    public async ValueTask<FakeRecurringScheduleMatch> MatchAsync(
      string scheduleKey,
      Expression<Func<CancellationToken, Task>> work,
      CancellationToken cancellationToken = default)
      => await this.MatchCoreAsync(scheduleKey, JobExpressionParser.Parse(work), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds recorded recurring schedules that match a ValueTask-returning job method call.
    /// </summary>
    public async ValueTask<FakeRecurringScheduleMatch> MatchAsync(
      string scheduleKey,
      Expression<Func<CancellationToken, ValueTask>> work,
      CancellationToken cancellationToken = default)
      => await this.MatchCoreAsync(scheduleKey, JobExpressionParser.Parse(work), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds recorded recurring schedules that match a Task-returning job method call with scheduler-supplied progress reporting.
    /// </summary>
    public async ValueTask<FakeRecurringScheduleMatch> MatchAsync(
      string scheduleKey,
      Expression<Func<CancellationToken, IProgress<decimal>, Task>> work,
      CancellationToken cancellationToken = default)
      => await this.MatchCoreAsync(scheduleKey, JobExpressionParser.Parse(work), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds recorded recurring schedules that match a ValueTask-returning job method call with scheduler-supplied progress reporting.
    /// </summary>
    public async ValueTask<FakeRecurringScheduleMatch> MatchAsync(
      string scheduleKey,
      Expression<Func<CancellationToken, IProgress<decimal>, ValueTask>> work,
      CancellationToken cancellationToken = default)
      => await this.MatchCoreAsync(scheduleKey, JobExpressionParser.Parse(work), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds recorded recurring schedules that match a Task-returning service method call.
    /// </summary>
    public async ValueTask<FakeRecurringScheduleMatch> MatchAsync<TService>(
      string scheduleKey,
      Expression<Func<TService, CancellationToken, Task>> work,
      CancellationToken cancellationToken = default)
      => await this.MatchCoreAsync(scheduleKey, JobExpressionParser.Parse(work), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds recorded recurring schedules that match a ValueTask-returning service method call.
    /// </summary>
    public async ValueTask<FakeRecurringScheduleMatch> MatchAsync<TService>(
      string scheduleKey,
      Expression<Func<TService, CancellationToken, ValueTask>> work,
      CancellationToken cancellationToken = default)
      => await this.MatchCoreAsync(scheduleKey, JobExpressionParser.Parse(work), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds recorded recurring schedules that match a Task-returning service method call with scheduler-supplied progress reporting.
    /// </summary>
    public async ValueTask<FakeRecurringScheduleMatch> MatchAsync<TService>(
      string scheduleKey,
      Expression<Func<TService, CancellationToken, IProgress<decimal>, Task>> work,
      CancellationToken cancellationToken = default)
      => await this.MatchCoreAsync(scheduleKey, JobExpressionParser.Parse(work), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds recorded recurring schedules that match a ValueTask-returning service method call with scheduler-supplied progress reporting.
    /// </summary>
    public async ValueTask<FakeRecurringScheduleMatch> MatchAsync<TService>(
      string scheduleKey,
      Expression<Func<TService, CancellationToken, IProgress<decimal>, ValueTask>> work,
      CancellationToken cancellationToken = default)
      => await this.MatchCoreAsync(scheduleKey, JobExpressionParser.Parse(work), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Removes all recorded recurring schedules.
    /// </summary>
    public void Clear()
    {
        lock (this._syncRoot)
        {
            this._schedules.Clear();
            this._triggeredJobs.Clear();
            this._nextEnqueueSequence = 0;
        }
    }

    private async ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateCoreAsync(
      string scheduleKey,
      string cronExpression,
      ParsedJob parsedJob,
      RecurringScheduleOptions? options,
      RecurringScheduleUpdateOptions? updateOptions,
      CancellationToken cancellationToken)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        CronSchedule.Validate(cronExpression);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveUpdateOptions = updateOptions ?? RecurringScheduleUpdateOptions.Default;
        var normalizedOptions = NormalizeOptions(options);
        var serializedArguments = await this.SerializeArgumentsAsync(parsedJob, cancellationToken).ConfigureAwait(false);
        var preparedSchedule = new PreparedSchedule(scheduleKey, cronExpression, parsedJob, serializedArguments, normalizedOptions);

        lock (this._syncRoot)
        {
            if (!this._schedules.TryGetValue(scheduleKey, out var existing))
            {
                this._schedules.Add(scheduleKey, CreateSchedule(preparedSchedule, isPaused: false, this.GetNextFireAtUtc(cronExpression)));
                return RecurringScheduleUpsertResult.Created;
            }

            var effectivePreparedSchedule = effectiveUpdateOptions.OverwriteCronExpression
              ? preparedSchedule
              : preparedSchedule with { CronExpression = existing.CronExpression };
            var effectiveIsPaused = !effectiveUpdateOptions.OverwritePausedState && existing.IsPaused;
            DateTimeOffset? effectiveNextFireAtUtc = effectiveIsPaused
              ? null
              : this.GetNextFireAtUtc(effectivePreparedSchedule.CronExpression);

            if (MatchesDefinition(existing, effectivePreparedSchedule) && existing.IsPaused == effectiveIsPaused)
            {
                return RecurringScheduleUpsertResult.Unchanged;
            }

            this._schedules[scheduleKey] = CreateSchedule(
              effectivePreparedSchedule,
              effectiveIsPaused,
              effectiveNextFireAtUtc);

            return RecurringScheduleUpsertResult.Updated;
        }
    }

    private async ValueTask<FakeRecurringScheduleMatch> MatchCoreAsync(
      string scheduleKey,
      ParsedJob parsedJob,
      CancellationToken cancellationToken)
    {
        SubmissionValidator.ValidateScheduleKey(scheduleKey);
        var serializedArguments = await this.SerializeArgumentsAsync(parsedJob, cancellationToken).ConfigureAwait(false);
        FakeRecurringSchedule[] scheduleSnapshot;

        lock (this._syncRoot)
        {
            scheduleSnapshot = [.. this._schedules.Values];
        }

        var matchedSchedules = scheduleSnapshot
            .Where(schedule => Matches(schedule, scheduleKey, parsedJob, serializedArguments))
            .ToArray();

        return new FakeRecurringScheduleMatch(matchedSchedules);
    }

    private async ValueTask<SerializedJobPayload> SerializeArgumentsAsync(
      ParsedJob parsedJob,
      CancellationToken cancellationToken)
    {
        var serializedArguments = await this._serializer
            .SerializeAsync(parsedJob.SerializableArguments, parsedJob.SerializableParameterTypes, cancellationToken)
            .ConfigureAwait(false);

        return JobPayloadSnapshot.Copy(serializedArguments);
    }

    private static FakeRecurringSchedule CreateSchedule(
      PreparedSchedule schedule,
      bool isPaused,
      DateTimeOffset? nextFireAtUtc)
      => new(
          schedule.ScheduleKey,
          schedule.CronExpression,
          isPaused,
          nextFireAtUtc,
          schedule.ParsedJob.ServiceType,
          schedule.ParsedJob.MethodName,
          [.. schedule.ParsedJob.MethodParameterTypeNames.Select(TypeNameFormatter.Resolve)],
          schedule.ParsedJob.MethodParameterBindings,
          schedule.ParsedJob.InvocationTargetKind,
          schedule.ParsedJob.SerializableParameterTypes,
          schedule.ParsedJob.SerializableArguments,
          schedule.SerializedArguments,
          schedule.Options);

    private DateTimeOffset GetNextFireAtUtc(string cronExpression)
      => CronSchedule.GetNextOccurrenceAfter(cronExpression, this._timeProvider.GetUtcNow());

    private FakeTriggeredJob CreateTriggeredJob(FakeRecurringSchedule schedule)
      => new(Guid.NewGuid(), this._nextEnqueueSequence++, schedule);

    private static bool Matches(
      FakeRecurringSchedule schedule,
      string scheduleKey,
      ParsedJob parsedJob,
      SerializedJobPayload serializedArguments)
      => string.Equals(schedule.ScheduleKey, scheduleKey, StringComparison.Ordinal)
        && schedule.ServiceType == parsedJob.ServiceType
        && string.Equals(schedule.MethodName, parsedJob.MethodName, StringComparison.Ordinal)
        && schedule.InvocationTargetKind == parsedJob.InvocationTargetKind
        && schedule.MethodParameterTypes.Select(TypeNameFormatter.Format).SequenceEqual(parsedJob.MethodParameterTypeNames, StringComparer.Ordinal)
        && schedule.MethodParameterBindings.SequenceEqual(parsedJob.MethodParameterBindings)
        && JobPayloadSnapshot.AreEqual(schedule.StoredSerializedArguments, serializedArguments);

    private static bool MatchesDefinition(
      FakeRecurringSchedule existing,
      PreparedSchedule candidate)
      => string.Equals(existing.CronExpression, candidate.CronExpression, StringComparison.Ordinal)
        && existing.Priority == candidate.Options.Priority
        && existing.OverlapMode == candidate.Options.OverlapMode
        && existing.ServiceType == candidate.ParsedJob.ServiceType
        && string.Equals(existing.MethodName, candidate.ParsedJob.MethodName, StringComparison.Ordinal)
        && existing.MethodParameterTypes.Select(TypeNameFormatter.Format).SequenceEqual(candidate.ParsedJob.MethodParameterTypeNames, StringComparer.Ordinal)
        && existing.InvocationTargetKind == candidate.ParsedJob.InvocationTargetKind
        && existing.MethodParameterBindings.SequenceEqual(candidate.ParsedJob.MethodParameterBindings)
        && JobPayloadSnapshot.AreEqual(existing.StoredSerializedArguments, candidate.SerializedArguments)
        && existing.ConcurrencyGroupKeys.SequenceEqual(candidate.Options.ConcurrencyGroupKeys ?? [], StringComparer.Ordinal)
        && existing.Tags.SequenceEqual(candidate.Options.Tags ?? [])
        && existing.RetryPolicy == candidate.Options.RetryPolicy;

    private static RecurringScheduleOptions NormalizeOptions(RecurringScheduleOptions? options)
      => new(
        options?.Priority ?? 0,
        SubmissionValidator.NormalizeConcurrencyGroupKeys(options?.ConcurrencyGroupKeys),
        NormalizeRetryPolicy(options?.RetryPolicy),
        options?.OverlapMode ?? RecurringOverlapMode.Skip,
        SubmissionValidator.NormalizeJobTags(options?.Tags));

    private static RetryPolicy? NormalizeRetryPolicy(RetryPolicy? retryPolicy)
    {
        SubmissionValidator.ValidateRetryPolicy(retryPolicy);

        return retryPolicy;
    }

    private sealed record PreparedSchedule(
      string ScheduleKey,
      string CronExpression,
      ParsedJob ParsedJob,
      SerializedJobPayload SerializedArguments,
      RecurringScheduleOptions Options);
}
