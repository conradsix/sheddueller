namespace Sheddueller.Testing;

using System.Collections.ObjectModel;

using Sheddueller.Enqueueing;
using Sheddueller.Serialization;
using Sheddueller.Storage;

/// <summary>
/// The job method call a fake recorded: its target method and serialized arguments. Recorded jobs, schedules, and
/// triggered jobs expose it through their public properties.
/// </summary>
internal sealed class FakeJobDefinition
{
    private readonly ParsedJob _parsedJob;
    private readonly SerializedJobPayload _serializedArguments;

    private FakeJobDefinition(
        ParsedJob parsedJob,
        SerializedJobPayload serializedArguments)
    {
        this._parsedJob = parsedJob;
        this._serializedArguments = ClonePayload(serializedArguments);
        this.MethodParameterTypes = ToReadOnlyCollection(parsedJob.MethodParameterTypes);
        this.MethodParameterBindings = ToReadOnlyCollection(parsedJob.MethodParameterBindings);
        this.SerializableParameterTypes = ToReadOnlyCollection(parsedJob.SerializableParameterTypes);
        this.SerializableArguments = ToReadOnlyCollection(parsedJob.SerializableArguments);
    }

    public Type ServiceType
      => this._parsedJob.ServiceType;

    public string MethodName
      => this._parsedJob.MethodName;

    public IReadOnlyList<Type> MethodParameterTypes { get; }

    public IReadOnlyList<JobMethodParameterBinding> MethodParameterBindings { get; }

    public JobInvocationTargetKind InvocationTargetKind
      => this._parsedJob.InvocationTargetKind;

    public IReadOnlyList<Type> SerializableParameterTypes { get; }

    public IReadOnlyList<object?> SerializableArguments { get; }

    /// <summary>
    /// Gets a copy of the serialized argument payload, so callers cannot change what is matched.
    /// </summary>
    public SerializedJobPayload SerializedArguments
      => ClonePayload(this._serializedArguments);

    /// <summary>
    /// Creates a definition for <paramref name="parsedJob"/>, serializing its arguments as the real enqueuer does.
    /// </summary>
    public static async ValueTask<FakeJobDefinition> CreateAsync(
        ParsedJob parsedJob,
        IJobPayloadSerializer serializer,
        CancellationToken cancellationToken)
    {
        var serializedArguments = await serializer
            .SerializeAsync(parsedJob.SerializableArguments, parsedJob.SerializableParameterTypes, cancellationToken)
            .ConfigureAwait(false);

        return new FakeJobDefinition(parsedJob, serializedArguments);
    }

    /// <summary>
    /// Returns whether both definitions call the same method with the same serialized arguments.
    /// </summary>
    public bool Matches(FakeJobDefinition other)
      => this.ServiceType == other.ServiceType
        && string.Equals(this.MethodName, other.MethodName, StringComparison.Ordinal)
        && this.InvocationTargetKind == other.InvocationTargetKind
        && this._parsedJob.MethodParameterTypeNames.SequenceEqual(other._parsedJob.MethodParameterTypeNames, StringComparer.Ordinal)
        && this.MethodParameterBindings.SequenceEqual(other.MethodParameterBindings)
        && string.Equals(this._serializedArguments.ContentType, other._serializedArguments.ContentType, StringComparison.Ordinal)
        && this._serializedArguments.Data.AsSpan().SequenceEqual(other._serializedArguments.Data);

    /// <summary>
    /// Creates the idempotency key the real enqueuer would store for this definition, or null for none.
    /// </summary>
    public string? CreateIdempotencyKey(JobIdempotencyKind idempotencyKind)
      => JobIdempotencyKeyGenerator.CreateKey(
          idempotencyKind,
          this._parsedJob,
          TypeNameFormatter.Format(this.ServiceType),
          this._serializedArguments);

    public static ReadOnlyCollection<T> ToReadOnlyCollection<T>(IReadOnlyList<T> values)
      => Array.AsReadOnly([.. values]);

    private static SerializedJobPayload ClonePayload(SerializedJobPayload payload)
      => new(payload.ContentType, [.. payload.Data]);
}
