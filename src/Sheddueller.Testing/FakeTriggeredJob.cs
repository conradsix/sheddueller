namespace Sheddueller.Testing;

using Sheddueller.Serialization;
using Sheddueller.Storage;

/// <summary>
/// Recorded job created by manually triggering a recurring schedule in <see cref="FakeRecurringScheduleManager"/>.
/// </summary>
public sealed class FakeTriggeredJob
{
    internal FakeTriggeredJob(
      Guid jobId,
      long enqueueSequence,
      FakeRecurringSchedule schedule)
    {
        this.JobId = jobId;
        this.EnqueueSequence = enqueueSequence;
        this.SourceScheduleKey = schedule.ScheduleKey;
        this.Definition = schedule.Definition;
        this.Priority = schedule.Priority;
        this.ConcurrencyGroupKeys = schedule.ConcurrencyGroupKeys;
        this.RetryPolicy = schedule.RetryPolicy;
        this.Tags = schedule.Tags;
    }

    /// <summary>
    /// Gets the job identifier returned by the trigger call.
    /// </summary>
    public Guid JobId { get; }

    /// <summary>
    /// Gets the zero-based enqueue sequence for this fake instance.
    /// </summary>
    public long EnqueueSequence { get; }

    /// <summary>
    /// Gets the source recurring schedule key.
    /// </summary>
    public string SourceScheduleKey { get; }

    /// <summary>
    /// Gets the service type that owns the triggered job method.
    /// </summary>
    public Type ServiceType => this.Definition.ServiceType;

    /// <summary>
    /// Gets the triggered job method name.
    /// </summary>
    public string MethodName => this.Definition.MethodName;

    /// <summary>
    /// Gets the target method parameter types.
    /// </summary>
    public IReadOnlyList<Type> MethodParameterTypes => this.Definition.MethodParameterTypes;

    /// <summary>
    /// Gets how each target method parameter is supplied when the job runs.
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
    /// Gets the priority cloned from the schedule template.
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Gets the concurrency group keys cloned from the schedule template.
    /// </summary>
    public IReadOnlyList<string> ConcurrencyGroupKeys { get; }

    /// <summary>
    /// Gets the retry policy cloned from the schedule template.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; }

    /// <summary>
    /// Gets the tags cloned from the schedule template.
    /// </summary>
    public IReadOnlyList<JobTag> Tags { get; }

    internal FakeJobDefinition Definition { get; }
}
