namespace Sheddueller.Testing;

using System.Collections.ObjectModel;

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
        this.ServiceType = schedule.ServiceType;
        this.MethodName = schedule.MethodName;
        this.MethodParameterTypes = ToReadOnlyCollection(schedule.MethodParameterTypes);
        this.MethodParameterBindings = ToReadOnlyCollection(schedule.MethodParameterBindings);
        this.InvocationTargetKind = schedule.InvocationTargetKind;
        this.SerializableParameterTypes = ToReadOnlyCollection(schedule.SerializableParameterTypes);
        this.SerializableArguments = ToReadOnlyCollection(schedule.SerializableArguments);
        this.SerializedArgumentsStorage = JobPayloadSnapshot.Copy(schedule.StoredSerializedArguments);
        this.Priority = schedule.Priority;
        this.ConcurrencyGroupKeys = ToReadOnlyCollection(schedule.ConcurrencyGroupKeys);
        this.RetryPolicy = schedule.RetryPolicy;
        this.Tags = ToReadOnlyCollection(schedule.Tags);
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
    public Type ServiceType { get; }

    /// <summary>
    /// Gets the triggered job method name.
    /// </summary>
    public string MethodName { get; }

    /// <summary>
    /// Gets the target method parameter types.
    /// </summary>
    public IReadOnlyList<Type> MethodParameterTypes { get; }

    /// <summary>
    /// Gets how each target method parameter is supplied when the job runs.
    /// </summary>
    public IReadOnlyList<JobMethodParameterBinding> MethodParameterBindings { get; }

    /// <summary>
    /// Gets how the target method is invoked.
    /// </summary>
    public JobInvocationTargetKind InvocationTargetKind { get; }

    /// <summary>
    /// Gets the parameter types included in the serialized argument payload.
    /// </summary>
    public IReadOnlyList<Type> SerializableParameterTypes { get; }

    /// <summary>
    /// Gets the evaluated argument values included in the serialized argument payload.
    /// </summary>
    public IReadOnlyList<object?> SerializableArguments { get; }

    /// <summary>
    /// Gets a copy of the serialized argument payload.
    /// </summary>
    public SerializedJobPayload SerializedArguments => JobPayloadSnapshot.Copy(this.SerializedArgumentsStorage);

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

    internal SerializedJobPayload StoredSerializedArguments => this.SerializedArgumentsStorage;

    private SerializedJobPayload SerializedArgumentsStorage { get; }

    private static ReadOnlyCollection<T> ToReadOnlyCollection<T>(IReadOnlyList<T> values)
      => Array.AsReadOnly([.. values]);
}
