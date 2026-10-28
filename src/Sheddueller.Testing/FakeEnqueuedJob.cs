namespace Sheddueller.Testing;

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
      FakeJobDefinition definition,
      JobSubmission submission,
      IReadOnlyList<Guid> prerequisiteJobIds)
    {
        this.JobId = jobId;
        this.EnqueueSequence = enqueueSequence;
        this.BatchId = batchId;
        this.BatchIndex = batchIndex;
        this.Definition = definition;
        this.Submission = submission;
        this.PrerequisiteJobIds = FakeJobDefinition.ToReadOnlyCollection(prerequisiteJobIds);
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
    public Type ServiceType => this.Definition.ServiceType;

    /// <summary>
    /// Gets the job method name.
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
    /// Gets the normalized submission options captured with this job.
    /// </summary>
    public JobSubmission Submission { get; }

    /// <summary>
    /// Gets the jobs in the same batch that must become terminal before this job may run.
    /// </summary>
    public IReadOnlyList<Guid> PrerequisiteJobIds { get; }

    internal FakeJobDefinition Definition { get; }
}
