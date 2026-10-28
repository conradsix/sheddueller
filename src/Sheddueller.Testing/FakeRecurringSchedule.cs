namespace Sheddueller.Testing;

using Sheddueller.Serialization;
using Sheddueller.Storage;

/// <summary>
/// Recorded recurring schedule definition captured by <see cref="FakeRecurringScheduleManager"/>.
/// </summary>
public sealed class FakeRecurringSchedule
{
    internal FakeRecurringSchedule(
      string scheduleKey,
      string cronExpression,
      bool isPaused,
      DateTimeOffset? nextFireAtUtc,
      FakeJobDefinition definition,
      RecurringScheduleOptions options)
    {
        this.ScheduleKey = scheduleKey;
        this.CronExpression = cronExpression;
        this.IsPaused = isPaused;
        this.NextFireAtUtc = nextFireAtUtc;
        this.Definition = definition;
        this.Priority = options.Priority;
        this.ConcurrencyGroupKeys = FakeJobDefinition.ToReadOnlyCollection(options.ConcurrencyGroupKeys ?? []);
        this.RetryPolicy = options.RetryPolicy;
        this.OverlapMode = options.OverlapMode;
        this.Tags = FakeJobDefinition.ToReadOnlyCollection(options.Tags ?? []);
    }

    /// <summary>
    /// Gets the recurring schedule key.
    /// </summary>
    public string ScheduleKey { get; }

    /// <summary>
    /// Gets the recurring schedule cron expression.
    /// </summary>
    public string CronExpression { get; }

    /// <summary>
    /// Gets a value indicating whether the schedule is paused.
    /// </summary>
    public bool IsPaused { get; }

    /// <summary>
    /// Gets the next scheduled fire time.
    /// </summary>
    public DateTimeOffset? NextFireAtUtc { get; }

    /// <summary>
    /// Gets the service type that owns the scheduled job method.
    /// </summary>
    public Type ServiceType => this.Definition.ServiceType;

    /// <summary>
    /// Gets the scheduled job method name.
    /// </summary>
    public string MethodName => this.Definition.MethodName;

    /// <summary>
    /// Gets the target method parameter types.
    /// </summary>
    public IReadOnlyList<Type> MethodParameterTypes => this.Definition.MethodParameterTypes;

    /// <summary>
    /// Gets how each target method parameter is supplied when a job materializes.
    /// </summary>
    public IReadOnlyList<JobMethodParameterBinding> MethodParameterBindings => this.Definition.MethodParameterBindings;

    /// <summary>
    /// Gets how the target method is invoked.
    /// </summary>
    public JobInvocationTargetKind InvocationTargetKind => this.Definition.InvocationTargetKind;

    /// <summary>
    /// Gets the parameter types included in the serialized argument payload.
    /// </summary>
    public IReadOnlyList<Type> SerializableParameterTypes => this.Definition.SerializableParameterTypes;

    /// <summary>
    /// Gets the evaluated argument values included in the serialized argument payload.
    /// </summary>
    public IReadOnlyList<object?> SerializableArguments => this.Definition.SerializableArguments;

    /// <summary>
    /// Gets a copy of the serialized argument payload.
    /// </summary>
    public SerializedJobPayload SerializedArguments => this.Definition.SerializedArguments;

    /// <summary>
    /// Gets the priority assigned to jobs materialized from this schedule.
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Gets concurrency group keys assigned to jobs materialized from this schedule.
    /// </summary>
    public IReadOnlyList<string> ConcurrencyGroupKeys { get; }

    /// <summary>
    /// Gets the retry policy assigned to jobs materialized from this schedule.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; }

    /// <summary>
    /// Gets the overlap mode used when an occurrence is due.
    /// </summary>
    public RecurringOverlapMode OverlapMode { get; }

    /// <summary>
    /// Gets tags assigned to jobs materialized from this schedule.
    /// </summary>
    public IReadOnlyList<JobTag> Tags { get; }

    internal FakeJobDefinition Definition { get; }

    internal RecurringScheduleInfo ToInfo()
      => new(
        this.ScheduleKey,
        this.CronExpression,
        this.IsPaused,
        this.OverlapMode,
        this.Priority,
        this.ConcurrencyGroupKeys,
        this.RetryPolicy,
        this.NextFireAtUtc)
      {
          Tags = this.Tags,
      };

    internal RecurringScheduleOptions ToOptions()
      => new(
        this.Priority,
        this.ConcurrencyGroupKeys,
        this.RetryPolicy,
        this.OverlapMode,
        this.Tags);

    internal FakeRecurringSchedule WithPaused(bool isPaused, DateTimeOffset? nextFireAtUtc)
      => new(
        this.ScheduleKey,
        this.CronExpression,
        isPaused,
        nextFireAtUtc,
        this.Definition,
        this.ToOptions());
}
