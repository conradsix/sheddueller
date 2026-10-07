namespace Sheddueller.Testing;

using System.Collections.ObjectModel;

using Sheddueller.Serialization;
using Sheddueller.Storage;

/// <summary>
/// Recorded work captured by <see cref="FakeJobEnqueuer"/>.
/// </summary>
public sealed class FakeEnqueuedJob
{
    internal FakeEnqueuedJob(
      Guid jobId,
      long enqueueSequence,
      Guid? batchId,
      int? batchIndex,
      Type serviceType,
      string methodName,
      IReadOnlyList<Type> methodParameterTypes,
      IReadOnlyList<JobMethodParameterBinding> methodParameterBindings,
      JobInvocationTargetKind invocationTargetKind,
      IReadOnlyList<Type> serializableParameterTypes,
      IReadOnlyList<object?> serializableArguments,
      SerializedJobPayload serializedArguments,
      JobSubmission submission,
      IReadOnlyList<Guid> prerequisiteJobIds)
    {
        this.JobId = jobId;
        this.EnqueueSequence = enqueueSequence;
        this.BatchId = batchId;
        this.BatchIndex = batchIndex;
        this.ServiceType = serviceType;
        this.MethodName = methodName;
        this.MethodParameterTypes = ToReadOnlyCollection(methodParameterTypes);
        this.MethodParameterBindings = ToReadOnlyCollection(methodParameterBindings);
        this.InvocationTargetKind = invocationTargetKind;
        this.SerializableParameterTypes = ToReadOnlyCollection(serializableParameterTypes);
        this.SerializableArguments = ToReadOnlyCollection(serializableArguments);
        this.SerializedArgumentsStorage = JobPayloadSnapshot.Copy(serializedArguments);
        this.Submission = submission;
        this.PrerequisiteJobIds = ToReadOnlyCollection(prerequisiteJobIds);
    }

    /// <summary>
    /// Gets the job identifier returned by the enqueue call.
    /// </summary>
    public Guid JobId { get; }

    /// <summary>
    /// Gets the zero-based enqueue sequence for this fake instance.
    /// </summary>
    public long EnqueueSequence { get; }

    /// <summary>
    /// Gets the batch identifier when the job was recorded by <see cref="IJobEnqueuer.EnqueueManyAsync"/>.
    /// </summary>
    public Guid? BatchId { get; }

    /// <summary>
    /// Gets the zero-based batch index when the job was recorded by <see cref="IJobEnqueuer.EnqueueManyAsync"/>.
    /// </summary>
    public int? BatchIndex { get; }

    /// <summary>
    /// Gets the service type that owns the job method.
    /// </summary>
    public Type ServiceType { get; }

    /// <summary>
    /// Gets the job method name.
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
    /// Gets the normalized submission options captured with this job.
    /// </summary>
    public JobSubmission Submission { get; }

    /// <summary>
    /// Gets the jobs in the same batch that must become terminal before this job may run.
    /// </summary>
    public IReadOnlyList<Guid> PrerequisiteJobIds { get; }

    internal SerializedJobPayload StoredSerializedArguments => this.SerializedArgumentsStorage;

    private SerializedJobPayload SerializedArgumentsStorage { get; }

    private static ReadOnlyCollection<T> ToReadOnlyCollection<T>(IReadOnlyList<T> values)
      => Array.AsReadOnly([.. values]);
}
